package us.copix.takxr.video;

import android.content.Context;
import android.graphics.SurfaceTexture;
import android.opengl.EGL14;
import android.opengl.EGLConfig;
import android.opengl.EGLContext;
import android.opengl.EGLDisplay;
import android.opengl.EGLSurface;
import android.opengl.GLES11Ext;
import android.opengl.GLES20;
import android.opengl.Matrix;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.util.Log;
import android.view.Surface;

import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.VideoSize;
import androidx.media3.exoplayer.ExoPlayer;
import androidx.media3.exoplayer.rtsp.RtspMediaSource;
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory;
import androidx.media3.exoplayer.source.MediaSource;

import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.FloatBuffer;

/**
 * ATAK-style RTSP/HLS via Media3 ExoPlayer.
 * <p>
 * Decodes into a SurfaceTexture on a private GLES context, blits EXTERNAL_OES →
 * RGBA, then exposes frames to Unity via glReadPixels. Avoids ImageReader/YUV
 * plane access, which SIGSEGVs on Samsung XR when Unity runs Vulkan.
 */
public final class TakXrExoPlayer {
    private static final String TAG = "TakXrExoPlayer";
    private static final int OUT_W = 640;
    private static final int OUT_H = 360;

    private final Context appContext;
    private final Handler mainHandler;
    private HandlerThread glThread;
    private Handler glHandler;

    private ExoPlayer player;
    private SurfaceTexture surfaceTexture;
    private Surface decoderSurface;
    private int oesTexId;
    private int program;
    private int aPos;
    private int aTex;
    private int uMtx;
    private int uTex;
    private FloatBuffer quadPos;
    private FloatBuffer quadTex;
    private final float[] stMatrix = new float[16];
    private final float[] identity = new float[16];

    private EGLDisplay eglDisplay = EGL14.EGL_NO_DISPLAY;
    private EGLContext eglContext = EGL14.EGL_NO_CONTEXT;
    private EGLSurface eglSurface = EGL14.EGL_NO_SURFACE;

    private volatile String status = "idle";
    private volatile String lastError = "";
    private volatile int width = OUT_W;
    private volatile int height = OUT_H;
    private volatile boolean playing;
    private volatile boolean frameAvailable;

    private final Object frameLock = new Object();
    private byte[] rgbaFront;
    private byte[] rgbaBack;
    private ByteBuffer readBuf;
    private volatile boolean frameReady;
    private volatile long frameSeq;
    private volatile boolean released;

    public TakXrExoPlayer(Context context) {
        this.appContext = context.getApplicationContext();
        this.mainHandler = new Handler(Looper.getMainLooper());
        Matrix.setIdentityM(identity, 0);
    }

    public String getStatus() { return status; }
    public String getLastError() { return lastError; }
    public int getWidth() { return width; }
    public int getHeight() { return height; }
    public boolean isPlaying() { return playing; }
    public long getFrameSeq() { return frameSeq; }

    /** Latest RGBA8888 frame copy, or null if none since last poll. */
    public byte[] pollRgbaFrame() {
        synchronized (frameLock) {
            if (!frameReady || rgbaFront == null) return null;
            frameReady = false;
            byte[] copy = new byte[rgbaFront.length];
            System.arraycopy(rgbaFront, 0, copy, 0, rgbaFront.length);
            return copy;
        }
    }

    public void start(final String url, final boolean forceRtpTcp) {
        if (url == null || url.length() == 0) {
            lastError = "empty url";
            status = "error";
            return;
        }
        Log.i(TAG, "start " + url + " tcp=" + forceRtpTcp);
        status = "connecting";
        lastError = "";
        released = false;
        mainHandler.post(() -> startOnMain(url, forceRtpTcp));
    }

    public void stop() {
        mainHandler.post(this::stopOnMain);
    }

    public void release() {
        released = true;
        mainHandler.post(() -> {
            stopOnMain();
            if (glHandler != null) {
                glHandler.post(this::releaseGl);
            }
            if (glThread != null) {
                glThread.quitSafely();
                glThread = null;
                glHandler = null;
            }
        });
    }

    private void startOnMain(String url, boolean forceRtpTcp) {
        stopPlayerOnly();
        ensureGlThread();
        status = "preparing";
        glHandler.post(() -> {
            try {
                ensureGl();
                final Surface surface = decoderSurface;
                mainHandler.post(() -> createPlayer(url, forceRtpTcp, surface));
            } catch (Exception e) {
                lastError = e.getMessage() != null ? e.getMessage() : "GL init failed";
                status = "error";
                Log.e(TAG, "GL init failed", e);
            }
        });
    }

    private void createPlayer(String url, boolean forceRtpTcp, Surface surface) {
        if (released || surface == null) return;
        try {
            width = OUT_W;
            height = OUT_H;
            player = new ExoPlayer.Builder(appContext).build();
            player.setVideoSurface(surface);
            player.addListener(new Player.Listener() {
                @Override
                public void onPlaybackStateChanged(int state) {
                    if (state == Player.STATE_BUFFERING) status = "buffering";
                    else if (state == Player.STATE_READY) {
                        status = "playing";
                        playing = true;
                    } else if (state == Player.STATE_ENDED) {
                        status = "ended";
                        playing = false;
                    }
                }

                @Override
                public void onPlayerError(PlaybackException error) {
                    lastError = error != null ? error.getMessage() : "player error";
                    status = "error";
                    playing = false;
                    Log.e(TAG, "player error: " + lastError, error);
                }

                @Override
                public void onVideoSizeChanged(VideoSize videoSize) {
                    if (videoSize != null && videoSize.width > 0 && videoSize.height > 0) {
                        Log.i(TAG, "video size " + videoSize.width + "x" + videoSize.height
                                + " (display " + OUT_W + "x" + OUT_H + ")");
                    }
                }
            });

            MediaItem item = MediaItem.fromUri(url);
            boolean isRtsp = url.regionMatches(true, 0, "rtsp", 0, 4);
            if (isRtsp) {
                RtspMediaSource.Factory factory = new RtspMediaSource.Factory()
                        .setForceUseRtpTcp(forceRtpTcp || url.contains(":443"));
                MediaSource src = factory.createMediaSource(item);
                player.setMediaSource(src);
            } else {
                MediaSource src = new DefaultMediaSourceFactory(appContext).createMediaSource(item);
                player.setMediaSource(src);
            }
            player.prepare();
            player.setPlayWhenReady(true);
        } catch (Exception e) {
            lastError = e.getMessage() != null ? e.getMessage() : "start failed";
            status = "error";
            Log.e(TAG, "start failed", e);
            stopPlayerOnly();
        }
    }

    private void stopOnMain() {
        stopPlayerOnly();
        playing = false;
        if (!"error".equals(status)) status = "idle";
        synchronized (frameLock) {
            frameReady = false;
        }
    }

    private void stopPlayerOnly() {
        playing = false;
        try {
            if (player != null) {
                player.stop();
                player.clearVideoSurface();
                player.release();
            }
        } catch (Exception ignored) { }
        player = null;
    }

    private void ensureGlThread() {
        if (glThread != null) return;
        glThread = new HandlerThread("TakXrExoGL");
        glThread.start();
        glHandler = new Handler(glThread.getLooper());
        glHandler.post(() -> glHandler.postDelayed(this::drainFrames, 33));
    }

    /** Pull SurfaceTexture frames on the GL thread (avoids ImageReader entirely). */
    private void drainFrames() {
        if (glThread == null) return;
        try {
            if (frameAvailable && surfaceTexture != null && eglDisplay != EGL14.EGL_NO_DISPLAY) {
                frameAvailable = false;
                renderFrame();
            }
        } catch (Exception e) {
            Log.w(TAG, "drainFrames: " + e.getMessage());
        }
        if (glHandler != null) {
            glHandler.postDelayed(this::drainFrames, 33);
        }
    }

    private void ensureGl() {
        if (eglDisplay != EGL14.EGL_NO_DISPLAY && decoderSurface != null) return;

        eglDisplay = EGL14.eglGetDisplay(EGL14.EGL_DEFAULT_DISPLAY);
        if (eglDisplay == EGL14.EGL_NO_DISPLAY) throw new RuntimeException("eglGetDisplay");
        int[] ver = new int[2];
        if (!EGL14.eglInitialize(eglDisplay, ver, 0, ver, 1))
            throw new RuntimeException("eglInitialize");

        int[] attribList = {
                EGL14.EGL_RED_SIZE, 8,
                EGL14.EGL_GREEN_SIZE, 8,
                EGL14.EGL_BLUE_SIZE, 8,
                EGL14.EGL_ALPHA_SIZE, 8,
                EGL14.EGL_RENDERABLE_TYPE, EGL14.EGL_OPENGL_ES2_BIT,
                EGL14.EGL_SURFACE_TYPE, EGL14.EGL_PBUFFER_BIT,
                EGL14.EGL_NONE
        };
        EGLConfig[] configs = new EGLConfig[1];
        int[] num = new int[1];
        if (!EGL14.eglChooseConfig(eglDisplay, attribList, 0, configs, 0, 1, num, 0) || num[0] == 0)
            throw new RuntimeException("eglChooseConfig");

        int[] ctxAttrib = { EGL14.EGL_CONTEXT_CLIENT_VERSION, 2, EGL14.EGL_NONE };
        eglContext = EGL14.eglCreateContext(eglDisplay, configs[0], EGL14.EGL_NO_CONTEXT, ctxAttrib, 0);
        if (eglContext == null) throw new RuntimeException("eglCreateContext");

        int[] surfAttrib = { EGL14.EGL_WIDTH, OUT_W, EGL14.EGL_HEIGHT, OUT_H, EGL14.EGL_NONE };
        eglSurface = EGL14.eglCreatePbufferSurface(eglDisplay, configs[0], surfAttrib, 0);
        if (eglSurface == null) throw new RuntimeException("eglCreatePbufferSurface");
        if (!EGL14.eglMakeCurrent(eglDisplay, eglSurface, eglSurface, eglContext))
            throw new RuntimeException("eglMakeCurrent");

        int[] tex = new int[1];
        GLES20.glGenTextures(1, tex, 0);
        oesTexId = tex[0];
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTexId);
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MIN_FILTER, GLES20.GL_LINEAR);
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_MAG_FILTER, GLES20.GL_LINEAR);
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_S, GLES20.GL_CLAMP_TO_EDGE);
        GLES20.glTexParameteri(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, GLES20.GL_TEXTURE_WRAP_T, GLES20.GL_CLAMP_TO_EDGE);

        surfaceTexture = new SurfaceTexture(oesTexId);
        surfaceTexture.setDefaultBufferSize(OUT_W, OUT_H);
        surfaceTexture.setOnFrameAvailableListener(st -> frameAvailable = true, glHandler);
        decoderSurface = new Surface(surfaceTexture);

        program = buildProgram();
        aPos = GLES20.glGetAttribLocation(program, "aPosition");
        aTex = GLES20.glGetAttribLocation(program, "aTexCoord");
        uMtx = GLES20.glGetUniformLocation(program, "uSTMatrix");
        uTex = GLES20.glGetUniformLocation(program, "sTexture");

        float[] pos = { -1f, -1f, 1f, -1f, -1f, 1f, 1f, 1f };
        float[] uv = { 0f, 1f, 1f, 1f, 0f, 0f, 1f, 0f };
        quadPos = ByteBuffer.allocateDirect(pos.length * 4).order(ByteOrder.nativeOrder()).asFloatBuffer();
        quadPos.put(pos).position(0);
        quadTex = ByteBuffer.allocateDirect(uv.length * 4).order(ByteOrder.nativeOrder()).asFloatBuffer();
        quadTex.put(uv).position(0);

        int need = OUT_W * OUT_H * 4;
        readBuf = ByteBuffer.allocateDirect(need).order(ByteOrder.nativeOrder());
        rgbaBack = new byte[need];
        rgbaFront = new byte[need];
        width = OUT_W;
        height = OUT_H;
        Log.i(TAG, "GL ready " + OUT_W + "x" + OUT_H);
    }

    private void renderFrame() {
        if (!EGL14.eglMakeCurrent(eglDisplay, eglSurface, eglSurface, eglContext)) return;
        try {
            surfaceTexture.updateTexImage();
            surfaceTexture.getTransformMatrix(stMatrix);
        } catch (Exception e) {
            Log.w(TAG, "updateTexImage: " + e.getMessage());
            return;
        }

        GLES20.glViewport(0, 0, OUT_W, OUT_H);
        GLES20.glClearColor(0f, 0f, 0f, 1f);
        GLES20.glClear(GLES20.GL_COLOR_BUFFER_BIT);
        GLES20.glUseProgram(program);
        GLES20.glActiveTexture(GLES20.GL_TEXTURE0);
        GLES20.glBindTexture(GLES11Ext.GL_TEXTURE_EXTERNAL_OES, oesTexId);
        GLES20.glUniform1i(uTex, 0);
        GLES20.glUniformMatrix4fv(uMtx, 1, false, stMatrix, 0);

        GLES20.glEnableVertexAttribArray(aPos);
        GLES20.glVertexAttribPointer(aPos, 2, GLES20.GL_FLOAT, false, 0, quadPos);
        GLES20.glEnableVertexAttribArray(aTex);
        GLES20.glVertexAttribPointer(aTex, 2, GLES20.GL_FLOAT, false, 0, quadTex);
        GLES20.glDrawArrays(GLES20.GL_TRIANGLE_STRIP, 0, 4);
        GLES20.glDisableVertexAttribArray(aPos);
        GLES20.glDisableVertexAttribArray(aTex);

        readBuf.position(0);
        GLES20.glReadPixels(0, 0, OUT_W, OUT_H, GLES20.GL_RGBA, GLES20.GL_UNSIGNED_BYTE, readBuf);
        readBuf.position(0);
        // GL origin is bottom-left — flip vertically into rgbaBack.
        int stride = OUT_W * 4;
        for (int y = 0; y < OUT_H; y++) {
            readBuf.position((OUT_H - 1 - y) * stride);
            readBuf.get(rgbaBack, y * stride, stride);
        }

        synchronized (frameLock) {
            byte[] tmp = rgbaFront;
            rgbaFront = rgbaBack;
            rgbaBack = tmp;
            frameReady = true;
            frameSeq++;
        }
        if (!"playing".equals(status)) status = "playing";
        playing = true;
    }

    private void releaseGl() {
        try {
            if (decoderSurface != null) decoderSurface.release();
        } catch (Exception ignored) { }
        decoderSurface = null;
        try {
            if (surfaceTexture != null) surfaceTexture.release();
        } catch (Exception ignored) { }
        surfaceTexture = null;
        if (oesTexId != 0) {
            int[] t = { oesTexId };
            try { GLES20.glDeleteTextures(1, t, 0); } catch (Exception ignored) { }
            oesTexId = 0;
        }
        if (program != 0) {
            try { GLES20.glDeleteProgram(program); } catch (Exception ignored) { }
            program = 0;
        }
        if (eglDisplay != EGL14.EGL_NO_DISPLAY) {
            EGL14.eglMakeCurrent(eglDisplay, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_SURFACE, EGL14.EGL_NO_CONTEXT);
            if (eglSurface != EGL14.EGL_NO_SURFACE) EGL14.eglDestroySurface(eglDisplay, eglSurface);
            if (eglContext != EGL14.EGL_NO_CONTEXT) EGL14.eglDestroyContext(eglDisplay, eglContext);
            EGL14.eglTerminate(eglDisplay);
        }
        eglDisplay = EGL14.EGL_NO_DISPLAY;
        eglContext = EGL14.EGL_NO_CONTEXT;
        eglSurface = EGL14.EGL_NO_SURFACE;
    }

    private static int buildProgram() {
        final String vs =
                "attribute vec2 aPosition;\n" +
                "attribute vec2 aTexCoord;\n" +
                "uniform mat4 uSTMatrix;\n" +
                "varying vec2 vTexCoord;\n" +
                "void main() {\n" +
                "  gl_Position = vec4(aPosition, 0.0, 1.0);\n" +
                "  vTexCoord = (uSTMatrix * vec4(aTexCoord, 0.0, 1.0)).xy;\n" +
                "}\n";
        final String fs =
                "#extension GL_OES_EGL_image_external : require\n" +
                "precision mediump float;\n" +
                "varying vec2 vTexCoord;\n" +
                "uniform samplerExternalOES sTexture;\n" +
                "void main() {\n" +
                "  gl_FragColor = texture2D(sTexture, vTexCoord);\n" +
                "}\n";
        int v = loadShader(GLES20.GL_VERTEX_SHADER, vs);
        int f = loadShader(GLES20.GL_FRAGMENT_SHADER, fs);
        int prog = GLES20.glCreateProgram();
        GLES20.glAttachShader(prog, v);
        GLES20.glAttachShader(prog, f);
        GLES20.glLinkProgram(prog);
        int[] link = new int[1];
        GLES20.glGetProgramiv(prog, GLES20.GL_LINK_STATUS, link, 0);
        if (link[0] == 0) {
            String log = GLES20.glGetProgramInfoLog(prog);
            GLES20.glDeleteProgram(prog);
            throw new RuntimeException("program link: " + log);
        }
        GLES20.glDeleteShader(v);
        GLES20.glDeleteShader(f);
        return prog;
    }

    private static int loadShader(int type, String src) {
        int shader = GLES20.glCreateShader(type);
        GLES20.glShaderSource(shader, src);
        GLES20.glCompileShader(shader);
        int[] compiled = new int[1];
        GLES20.glGetShaderiv(shader, GLES20.GL_COMPILE_STATUS, compiled, 0);
        if (compiled[0] == 0) {
            String log = GLES20.glGetShaderInfoLog(shader);
            GLES20.glDeleteShader(shader);
            throw new RuntimeException("shader: " + log);
        }
        return shader;
    }
}
