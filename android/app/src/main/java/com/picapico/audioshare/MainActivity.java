package com.picapico.audioshare;

import android.annotation.SuppressLint;
import android.content.ComponentName;
import android.content.Context;
import android.content.Intent;
import android.content.ServiceConnection;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.content.res.Configuration;
import android.graphics.Color;
import android.media.AudioManager;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.util.Log;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.LinearLayout;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;

public class MainActivity extends AppCompatActivity {
    private static final String TAG = "AudioShare";
    private TcpService tcpService = null;
    private boolean isBound = false;
    private TextView ipAddress;
    private LinearLayout connectedLayout;
    private LinearLayout unConnectedLayout;
    private final TcpService.MessageListener messageListener = () -> {
        setConnectionStatus();
        setListenStatus();
    };

    private final ServiceConnection connection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName className, IBinder service) {
            TcpService.TcpBinder binder = (TcpService.TcpBinder) service;
            tcpService = binder.getService();
            tcpService.setAudioManager((AudioManager)getSystemService(Context.AUDIO_SERVICE));
            tcpService.setMessageListener(messageListener);
            isBound = true;
            setConnectionStatus();
            setListenStatus();
        }

        @Override
        public void onServiceDisconnected(ComponentName arg0) {
            isBound = false;
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        setStatusBarTransparent();
        setVersionName();
        ipAddress = findViewById(R.id.ipAddress);
        connectedLayout = findViewById(R.id.connected);
        unConnectedLayout = findViewById(R.id.unconnected);
        Intent intent = new Intent(this, TcpService.class);
        startService(intent);
        bindService(intent, connection, Context.BIND_AUTO_CREATE);
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        // 解绑服务
        if (isBound) {
            unbindService(connection);
            isBound = false;
        }
    }

    private void setVersionName(){
        PackageManager packageManager = getPackageManager();
        String packageName = getPackageName();
        try {
            PackageInfo packageInfo = packageManager.getPackageInfo(packageName, 0);
            TextView versionName = findViewById(R.id.versionName);
            versionName.setText(packageInfo.versionName);
        } catch (PackageManager.NameNotFoundException e) {
            Log.e(TAG, "set version error: " + e);
        }
    }

    private void setConnectionStatus(){
        if(tcpService == null) return;
        runOnUiThread(() -> {
            boolean playing = tcpService.getPlaying();
            connectedLayout.setVisibility(playing ? View.VISIBLE : View.GONE);
            unConnectedLayout.setVisibility(playing ? View.GONE : View.VISIBLE);
        });
    }

    @SuppressLint("SetTextI18n")
    private void setListenStatus(){
        if(tcpService == null) return;
        runOnUiThread(() -> {
            int port = tcpService.getListenPort();
            String ip = NetworkUtils.getIpAddress(this);
            ipAddress.setText(ip + ":" + port);
        });
    }

    private void setStatusBarTransparent() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Window window = getWindow();
            window.clearFlags(WindowManager.LayoutParams.FLAG_TRANSLUCENT_STATUS
                    | WindowManager.LayoutParams.FLAG_TRANSLUCENT_NAVIGATION);
            window.getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                    | View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
            window.addFlags(WindowManager.LayoutParams.FLAG_DRAWS_SYSTEM_BAR_BACKGROUNDS);
            window.setStatusBarColor(Color.TRANSPARENT);
        }
        setAndroidNativeLightStatusBar();
    }

    private void setAndroidNativeLightStatusBar() {
        boolean dark = isDarkMode();
        View decor = this.getWindow().getDecorView();
        if (!dark) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
                        View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR);
            }
        } else {
            decor.setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN |
                    View.SYSTEM_UI_FLAG_LAYOUT_STABLE);
        }
    }

    private boolean isDarkMode(){
        int nightModeFlags = getResources().getConfiguration().uiMode &
                        Configuration.UI_MODE_NIGHT_MASK;
        return nightModeFlags == Configuration.UI_MODE_NIGHT_YES;
    }
}