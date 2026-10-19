package us.copix.takxr.update;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;

import androidx.core.content.FileProvider;

import java.io.File;

/** Hands a downloaded APK to the system package installer. */
public final class ApkInstaller {
    private ApkInstaller() {}

    /**
     * @return "started" when the installer activity opened,
     *         "need-permission" when the user must allow installs from this app,
     *         "missing" when the file is not there
     */
    public static String install(Activity activity, String apkPath) {
        File file = new File(apkPath);
        if (!file.isFile()) return "missing";

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O
                && !activity.getPackageManager().canRequestPackageInstalls()) {
            Intent settings = new Intent(
                    Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                    Uri.parse("package:" + activity.getPackageName()));
            settings.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
            activity.startActivity(settings);
            return "need-permission";
        }

        Uri uri = FileProvider.getUriForFile(
                activity,
                activity.getPackageName() + ".update.fileprovider",
                file);
        Intent intent = new Intent(Intent.ACTION_VIEW);
        intent.setDataAndType(uri, "application/vnd.android.package-archive");
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        intent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
        activity.startActivity(intent);
        return "started";
    }
}
