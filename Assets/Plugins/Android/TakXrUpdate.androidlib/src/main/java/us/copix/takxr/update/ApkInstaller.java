package us.copix.takxr.update;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/** Hands a downloaded APK to the system package installer. */
public final class ApkInstaller {
    private ApkInstaller() {}

    /**
     * @return "started" when the installer confirmation was requested,
     *         "need-permission" when the user must allow installs from this app,
     *         "missing" when the file is not there
     */
    public static String install(Activity activity, String apkPath) {
        File file = new File(apkPath);
        if (!file.isFile()) return "missing";

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                && !activity.getPackageManager().canRequestPackageInstalls()) {
            Intent settings = new Intent(
                    android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    android.net.Uri.parse("package:" + activity.getPackageName()));
            settings.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(settings);
            return "need-permission";
        }

        try {
            PackageInstaller installer = activity.getPackageManager().getPackageInstaller();
            PackageInstaller.SessionParams params = new PackageInstaller.SessionParams(
                    PackageInstaller.SessionParams.MODE_FULL_INSTALL);
            params.setSize(file.length());
            int sessionId = installer.createSession(params);
            PackageInstaller.Session session = installer.openSession(sessionId);
            try (InputStream in = new FileInputStream(file);
                 OutputStream out = session.openWrite("base.apk", 0, file.length())) {
                byte[] buffer = new byte[65536];
                int read;
                while ((read = in.read(buffer)) != -1) {
                    out.write(buffer, 0, read);
                }
                session.fsync(out);
            }
            Intent callback = new Intent(activity, InstallResultReceiver.class);
            callback.setAction("us.copix.takxr.update.INSTALL_RESULT");
            int flags = PendingIntent.FLAG_UPDATE_CURRENT;
            if (Build.VERSION.SDK_INT >= 31) {
                flags |= PendingIntent.FLAG_MUTABLE;
            }
            PendingIntent pending = PendingIntent.getBroadcast(activity, sessionId, callback, flags);
            session.commit(pending.getIntentSender());
            session.close();
            return "started";
        } catch (Exception ex) {
            return "failed";
        }
    }
}
