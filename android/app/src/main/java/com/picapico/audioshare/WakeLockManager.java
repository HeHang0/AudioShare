package com.picapico.audioshare;

import android.annotation.SuppressLint;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.PowerManager;
import android.util.Log;

import androidx.core.content.ContextCompat;

public class WakeLockManager {
    private static final String TAG = "AudioShareBootReceiver";
    private PowerManager.WakeLock wakeLock = null;
    private final Context context;
    public WakeLockManager(Context context) {
        this.context = context;
    }

    public boolean hasWakeLockPermission() {
        return ContextCompat.checkSelfPermission(
                context, android.Manifest.permission.WAKE_LOCK) ==
                PackageManager.PERMISSION_GRANTED;
    }

    @SuppressLint("WakelockTimeout")
    public void acquireWakeLock() {
        if(!hasWakeLockPermission()) return;
        PowerManager powerManager = (PowerManager) context.getSystemService(Context.POWER_SERVICE);
        wakeLock = powerManager.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, TcpService.class.getName());
        if (null != wakeLock)  {
            wakeLock.acquire();
            Log.i(TAG, "acquire wake lock");
        }
    }

    public void releaseWakeLock() {
        if (wakeLock != null && wakeLock.isHeld()) {
            wakeLock.release();
            wakeLock = null;
            Log.i(TAG, "release wake lock");
        }
    }
}