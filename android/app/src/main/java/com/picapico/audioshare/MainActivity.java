package com.picapico.audioshare;

import android.Manifest;
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
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.IBinder;
import android.util.Log;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;
import android.widget.CompoundButton;
import android.widget.TextView;

import androidx.appcompat.app.AppCompatActivity;
import androidx.appcompat.widget.SwitchCompat;
import androidx.core.app.ActivityCompat;

import java.net.Inet6Address;
import java.net.InetAddress;
import java.util.List;

public class MainActivity extends AppCompatActivity {
    private static final String TAG = "AudioShare";
    private TcpService tcpService = null;
    private boolean isBound = false;
    private TextView ipAddress;
    private SwitchCompat managerSwitch;
    private TextView managerText;
    private String versionName;
    private SwitchCompat connectionSwitch;
    private TextView connectionText;
    private final TcpService.MessageListener messageListener = () -> {
        setConnectionStatus();
        setListenStatus();
    };

    private final ServiceConnection connection = new ServiceConnection() {
        @Override
        public void onServiceConnected(ComponentName className, IBinder service) {
            TcpService.TcpBinder binder = (TcpService.TcpBinder) service;
            tcpService = binder.getService();
            tcpService.setAudioManager((AudioManager) getSystemService(Context.AUDIO_SERVICE));
            tcpService.setVersionName(versionName);
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
        managerSwitch = findViewById(R.id.managerSwitch);
        managerText = findViewById(R.id.managerText);
        connectionSwitch = findViewById(R.id.connectionSwitch);
        connectionText = findViewById(R.id.connectionText);
        Intent intent = new Intent(this, TcpService.class);
        startService(intent);
        bindService(intent, connection, Context.BIND_AUTO_CREATE);
        managerText.setOnClickListener(this::onManagerClick);
        findViewById(R.id.imageView).setOnClickListener(this::onManagerClick);
        ActivityCompat.requestPermissions(this, new String[]{
                Manifest.permission.INTERNET,
                Manifest.permission.ACCESS_NETWORK_STATE,
                Manifest.permission.ACCESS_WIFI_STATE
        }, 1);
        managerSwitch.setOnClickListener(this::onHttpServerRunningChanged);
    }

    private void onHttpServerRunningChanged(View v) {
        if(tcpService == null) return;
        tcpService.setHttpRunning(managerSwitch.isChecked());
    }

    private void onManagerClick(View e){
        try {
            Uri uri = Uri.parse(managerText.getText().toString());
            if(uri.isAbsolute()){
                Intent intent = new Intent(Intent.ACTION_VIEW, uri);
                startActivity(intent);
            }
        }catch (Exception ignore){}
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (isBound) {
            unbindService(connection);
            isBound = false;
        }
    }
    @SuppressLint("SetTextI18n")
    private void setVersionName(){
        PackageManager packageManager = getPackageManager();
        String packageName = getPackageName();
        try {
            PackageInfo packageInfo = packageManager.getPackageInfo(packageName, 0);
            TextView versionName = findViewById(R.id.versionName);
            versionName.setText(packageInfo.versionName+"\n\n");
            this.versionName = packageInfo.versionName;
        } catch (PackageManager.NameNotFoundException e) {
            Log.e(TAG, "set version error: " + e);
        }
    }

    private void setConnectionStatus(){
        if(tcpService == null) return;
        runOnUiThread(() -> {
            boolean playing = tcpService.getPlaying();
            connectionText.setText(playing ? R.string.connected : R.string.unconnected);
            connectionSwitch.setChecked(playing);
        });
    }

    @SuppressLint("SetTextI18n")
    private void setListenStatus(){
        if(tcpService == null) return;
        runOnUiThread(() -> {
            int port = tcpService.getListenPort();
            int httpPort = tcpService.getHttpPort();
            StringBuilder sb = new StringBuilder();
            List<InetAddress> ips = NetworkUtils.getAllInetAddress();
            String ipv4 = "";
            if(ips.isEmpty()){
                ipv4 = NetworkUtils.getIpAddress(this);
                sb.append(ipv4).append(":").append(port).append("\n");
            }else {
                for (InetAddress ip: ips) {
                    String address = ip.getHostAddress();
                    if(ip instanceof Inet6Address) {
                        address = "[" + address + "]";
                    }else {
                        ipv4 = address;
                    }
                    sb.append(address).append(":").append(port).append("\n");
                }
            }
            ipAddress.setText(sb.toString().trim());
            if(ipv4 == null || ipv4.isEmpty()) ipv4 = "127.0.0.1";
            String httpAddress = "http://" + ipv4;
            if(httpPort != 80){
                httpAddress += ":" + httpPort;
            }
            if(tcpService.getHttpRunning()){
                managerText.setText(httpAddress);
                managerSwitch.setChecked(true);
            }else {
                managerText.setText(R.string.musiche_closed);
                managerSwitch.setChecked(false);
            }
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