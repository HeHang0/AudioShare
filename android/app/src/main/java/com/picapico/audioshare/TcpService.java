package com.picapico.audioshare;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.media.AudioAttributes;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Binder;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;
import androidx.core.content.ContextCompat;

import com.phicomm.speaker.player.light.PlayerVisualizer;
import com.picapico.audioshare.musiche.player.AudioPlayer;
import com.picapico.audioshare.musiche.HttpServer;
import com.picapico.audioshare.musiche.notification.NotificationService;

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Timer;
import java.util.TimerTask;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class TcpService extends NotificationService {
    private static final String TAG = "AudioShareService";
    private static final String HEAD = "picapico-audio-share";
    public  static final String CHANNEL_ID = "com.picapico.audio_share";
    public static int NOTIFICATION_ID = 1;
    private final IBinder binder = new TcpBinder();
    private MessageListener mListener;
    private LocalServerSocket localServerSocket = null;
    private ServerSocket serverSocket = null;
    private AudioManager mAudioManager = null;
    private AudioTrack mAudioTrack = null;
    private OutputStream mSocketOutputStream = null;
    private int maxAudioVolume = 15;

    private boolean isWriting = false;
    private WakeLockManager mWakeLockManager;
    private final ExecutorService mExecutorService = Executors.newSingleThreadExecutor();
    private final Handler mHandler = new Handler(Looper.getMainLooper());
    private HttpServer httpServer;
    @Override
    public void onCreate() {
        super.onCreate();
        mWakeLockManager = new WakeLockManager(this);
        Log.i(TAG, "Service on created");
        new Thread(this::startLocalServer).start();
        new Thread(this::startServer).start();
        this.startHttpServer();
        this.startBroadcastTimer();
    }
    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Log.i(TAG, "Service on start command");
        return START_STICKY;
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        super.onTaskRemoved(rootIntent);
        Log.i(TAG, "Service on task removed");
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        stopForeground(true);
        stopAudio();
        Log.i(TAG, "Service on destroy");
        try {
            if(localServerSocket != null){
                localServerSocket.close();
                localServerSocket = null;
            }
        } catch (IOException e) {
            Log.e(TAG, "close local server error: " + e);
        }
        try {
            if(serverSocket != null){
                serverSocket.close();
                serverSocket = null;
            }
        } catch (IOException e) {
            Log.e(TAG, "close tcp server error: " + e);
        }
        httpServer.stop();
    }

    private byte readHead(InputStream stream) throws IOException {
        int bufferLength = HEAD.length();
        byte[] buffer = new byte[bufferLength];
        int offset = 0;
        int bytesRead;
        while (offset < bufferLength &&
                (bytesRead = stream.read(buffer, offset, bufferLength - offset)) != -1){
            offset += bytesRead;
        }
        if(new String(buffer).equalsIgnoreCase(HEAD)){
            while ((bytesRead = stream.read(buffer, 0, 1)) != -1){
                if(bytesRead >= 1) return buffer[0];
            }
        }
        return 0;
    }

    private int readInt(InputStream stream) throws IOException {
        byte[] buffer = new byte[4];
        int offset = 0;
        int bytesRead = 0;
        while (offset < 4 &&
                (bytesRead = stream.read(buffer, offset, 4 - offset)) != -1){
            offset += bytesRead;
        }
        if(bytesRead < 0){
            throw new IOException("read stream eol.");
        }
        return parseInt(buffer);
    }

    private void processControlStream(byte command, InputStream stream) {
        if(command == 2){
            try {
                int volume = readInt(stream);
                volume = maxAudioVolume * volume / 100;
                setVolume(volume);
            } catch (IOException e) {
                Log.e(TAG, "read volume error: " + e);
            }
        }else if(command == 3) {
            PlayerVisualizer.updateTimeMillis();
        }else if(command == 4) {
            if(getPlaying() && getPlayerCloser() != null){
                try {
                    getPlayerCloser().close();
                } catch (IOException ignored) {
                }
                setPlayerCloser(null);
                for (int i = 0; i < 10; i++) {
                    if(!getPlaying()) break;
                    try {
                        Thread.sleep(50);
                    } catch (InterruptedException ignored) {
                    }
                }
            }
        }
    }

    private void setVolume(int volume) {
        mHandler.post(() -> {
            try {
                Log.i(TAG, "set music volume " + volume);
                mAudioManager.setStreamVolume(
                        AudioManager.STREAM_MUSIC,
                        volume,
                        AudioManager.FLAG_SHOW_UI);
            } catch (Exception ignored){
            }
        });
    }

    private void processSocketClient(Closeable socket) throws IOException {
        InputStream stream;
        OutputStream outputStream;
        boolean isLocal = false;
        if (socket instanceof LocalSocket){
            stream = ((LocalSocket)socket).getInputStream();
            outputStream = ((LocalSocket)socket).getOutputStream();
            isLocal = true;
        }else if (socket instanceof Socket){
            stream = ((Socket)socket).getInputStream();
            outputStream = ((Socket)socket).getOutputStream();
        }else {
            return;
        }
        byte command = readHead(stream);
        Log.i(TAG, "client connected: " + command);
        if(command == 1 && !getPlaying()){
            int sampleRate = readInt(stream);
            int channel = readInt(stream);
            int audioFormat = AudioFormat.ENCODING_PCM_16BIT;
            int bufferSizeInBytes = AudioTrack.getMinBufferSize(sampleRate, channel, audioFormat);
            if(isLocal){
                ((LocalSocket)socket).setReceiveBufferSize(bufferSizeInBytes);
            }else {
                ((Socket)socket).setReceiveBufferSize(bufferSizeInBytes);
            }
            new Thread(() -> playAudio(
                    sampleRate,
                    channel,
                    audioFormat,
                    bufferSizeInBytes,
                    socket,
                    stream,
                    outputStream
            )).start();
        }else {
            processControlStream(command, stream);
            socket.close();
        }
    }

    private void startLocalServer(){
        Log.i(TAG, "prepare start local server");
        try {
            localServerSocket = new LocalServerSocket(HEAD);
            while (localServerSocket != null){
                LocalSocket clientSocket = null;
                try {
                    clientSocket = localServerSocket.accept();
                    Log.i(TAG, "local server client accept");
                    processSocketClient(clientSocket);
                } catch (Exception e) {
                    if(clientSocket != null){
                        try {
                            clientSocket.close();
                        } catch (Exception ignored) {
                        }
                    }
                    Log.e(TAG, "process local client error: " + e);
                    e.printStackTrace();
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "start local server error: " + e);
        } finally {
            try {
                if(localServerSocket != null) {
                    localServerSocket.close();
                }
            } catch (IOException e) {
                Log.e(TAG, "close local server error: " + e);
            }
        }
    }
    private void startHttpServer(){
        Log.i(TAG, "prepare http start server");
        int port = NetworkUtils.getFreePort(Build.MANUFACTURER.equalsIgnoreCase("phicomm") ? 8090 : 8080);
        this.setHttpPort(port);
        httpServer = new HttpServer(getApplicationContext(), port).start();
        httpServer.getAudioPlayer().setMediaMetaChangedListener(new AudioPlayer.OnMediaMetaChangedListener() {
            @Override
            public void onMediaMetaChanged(boolean playing, int position) {
                setMetaData(playing, position);
            }

            @Override
            public void onMediaMetaChanged(String title, String artist, String album, String artwork, boolean lover, boolean playing, int position, int duration) {
                setMetaData(title, artist, album, artwork, lover, playing, position, duration);
            }
        });
        this.setMediaSessionCallback(httpServer.getAudioPlayer().getNotificationCallback());
        httpServer.setSharedPreferences(getSharedPreferences("config", Context.MODE_PRIVATE));
        httpServer.setAssetManager(getAssets());
    }
    private void startServer(){
        Log.i(TAG, "prepare tcp start server");
        try {
            int port = NetworkUtils.getFreePort();
            serverSocket = new ServerSocket(port);
            if(mListener != null){
                mListener.onMessage();
            }
            setListenPort(port);
            while (serverSocket != null && !serverSocket.isClosed()){
                Socket clientSocket = null;
                try {
                    clientSocket = serverSocket.accept();
                    clientSocket.setTcpNoDelay(true);
                    processSocketClient(clientSocket);
                } catch (Exception e) {
                    if(clientSocket != null){
                        try {
                            clientSocket.close();
                        } catch (Exception ignored) {
                        }
                    }
                    Log.e(TAG, "accept tcp server error: " + e);
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "start tcp server error: " + e);
        } finally {
            try {
                if(serverSocket != null) {
                    serverSocket.close();
                }
            } catch (IOException e) {
                Log.e(TAG, "close tcp server error: " + e);
            }
        }
    }

    private boolean isPlaying = false;
    private Closeable playerCloser = null;

    private synchronized void setPlaying(boolean playing) {
        isPlaying = playing;
    }

    private synchronized void setPlayerCloser(Closeable closer) {
        playerCloser = closer;
    }

    private synchronized Closeable getPlayerCloser() {
        return playerCloser;
    }

    public synchronized boolean getPlaying() {
        return isPlaying;
    }

    private int listenPort = 8088;

    private synchronized void setListenPort(int listenPort) {
        this.listenPort = listenPort;
    }

    public synchronized int getListenPort() {
        return listenPort;
    }

    private int httpPort = 8088;

    private synchronized void setHttpPort(int httpPort) {
        this.httpPort = httpPort;
    }

    public synchronized int getHttpPort() {
        return httpPort;
    }

    public void setMessageListener(MessageListener listener){
        mListener = listener;
    }

    public void setAudioManager(AudioManager audioManager){
        mAudioManager = audioManager;
        maxAudioVolume = mAudioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
        httpServer.setAudioManager(audioManager);
    }

    public void setVersionName(String versionName){
        httpServer.setVersionName(versionName);
    }

    private void playAudio(int sampleRateInHz, int channelConfig, int audioEncoding, int bufferSizeInBytes, Closeable closer, InputStream inputStream, OutputStream outputStream){
        if(getPlaying()) return;
        setPlaying(true);
        setPlayerCloser(closer);
        if(mListener != null){
            mListener.onMessage();
        }
        try {
            initNotification();
            mWakeLockManager.acquireWakeLock();
            AudioFormat audioFormat = new AudioFormat.Builder()
                    .setChannelMask(channelConfig)
                    .setEncoding(audioEncoding)
                    .setSampleRate(sampleRateInHz)
                    .build();
            AudioAttributes.Builder audioAttributes = new AudioAttributes.Builder()
                    .setLegacyStreamType(AudioManager.STREAM_MUSIC)
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC);
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
                audioAttributes.setFlags(AudioAttributes.FLAG_LOW_LATENCY);
            }
            httpServer.getAudioPlayer().pause();
            mAudioTrack = new AudioTrack(
                    audioAttributes.build(),
                    audioFormat,
                    bufferSizeInBytes,
                    AudioTrack.MODE_STREAM,
                    AudioManager.AUDIO_SESSION_ID_GENERATE);
            setVolume(0);
            byte[] buffer = new byte[bufferSizeInBytes];
            int dataLength;
            mAudioTrack.play();
            PlayerVisualizer.startBase(mAudioTrack.getAudioSessionId());
            Log.i(TAG, "play audio ready to read");
            outputStream.write(new byte[1]);
            outputStream.flush();
            mSocketOutputStream = outputStream;
            DataInputStream stream = new DataInputStream(inputStream);
            setWriting(false);
            while (true) {
                try {
                    stream.readFully(buffer, 0, 4);
                    dataLength = parseInt(buffer);
                    if(dataLength == 0) {
                        Log.i(TAG, "play audio heartbeat");
                        continue;
                    }
                    if(dataLength > buffer.length) {
                        buffer = new byte[dataLength];
                    }
                    stream.readFully(buffer, 0, dataLength);
                } catch (Exception e){
                    break;
                }
                if(getWriting()) {
                    Log.w(TAG, "write audio busy");
                    continue;
                }
                if(httpServer.getAudioPlayer().isPlaying()) {
                    Log.w(TAG, "write audio playing");
                    continue;
                }
                byte[] finalBuffer = buffer;
                int finalDataLength = dataLength;
                setWriting(true);
                mExecutorService.execute(() -> {
                    mAudioTrack.write(finalBuffer, 0, finalDataLength);
                    mHandler.post(() -> setWriting(false));
                });
            }
        } catch (Exception e) {
            Log.e(TAG, "play audio error: " + e);
        } finally {
            PlayerVisualizer.stopBase();
            try {
                inputStream.close();
            } catch (Exception e) {
                Log.e(TAG, "stop stream error: " + e);
            }
            try {
                closer.close();
            } catch (Exception e) {
                Log.e(TAG, "close audio socket error: " + e);
            }
        }
        stopAudio();
        stopSocketOutputStream();
        stopForeground(true);
        mWakeLockManager.releaseWakeLock();
        setPlaying(false);
        if(mListener != null){
            mListener.onMessage();
        }
        Log.i(TAG, "play audio ended");
    }

    private void stopAudio(){
        try {
            if(mAudioTrack != null) {
                mAudioTrack.pause();
                mAudioTrack.stop();
                mAudioTrack.flush();
                mAudioTrack.release();
                mAudioTrack = null;
            }
        } catch (Exception e) {
            Log.e(TAG, "stop audio error: " + e);
        }
    }

    private void stopSocketOutputStream(){
        try {
            if(mSocketOutputStream != null) {
                mSocketOutputStream.flush();
                mSocketOutputStream.close();
                mSocketOutputStream = null;
            }
        } catch (Exception e) {
            Log.e(TAG, "stop output stream error: " + e);
        }
    }
    private void createNotificationChannel(){
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
            NotificationChannel serviceChannel = new NotificationChannel(
                    CHANNEL_ID,
                    getResources().getString(R.string.app_name),
                    NotificationManager.IMPORTANCE_LOW
            );
            serviceChannel.enableLights(false);
            serviceChannel.enableVibration(false);
            NotificationManager manager = getSystemService(NotificationManager.class);
            if(manager != null) manager.createNotificationChannel(serviceChannel);
        }
    }
    private void initNotification(){
        createNotificationChannel();
        int flag = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            flag = PendingIntent.FLAG_IMMUTABLE;
        }
        Intent infoIntent = new Intent(this, BootReceiver.class);
        PendingIntent pendingInfo = PendingIntent.getActivity(this, 0, infoIntent, flag);
        Notification mNotification = new NotificationCompat.Builder(this, CHANNEL_ID)
                .setContentIntent(pendingInfo)
                .setSmallIcon(R.mipmap.ic_launcher)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setPriority(NotificationCompat.PRIORITY_MAX)
                .setContentTitle(getResources().getString(R.string.app_name))
                .setContentText(getResources().getString(R.string.app_name))
                .build();
        if(ContextCompat.checkSelfPermission(
                this, android.Manifest.permission.WAKE_LOCK) ==
                PackageManager.PERMISSION_GRANTED) {
            startForeground(NOTIFICATION_ID, mNotification);
        }
    }
    private synchronized void setWriting(boolean writing) {
        isWriting = writing;
    }

    public synchronized boolean getWriting() {
        return isWriting;
    }

    private void startBroadcastTimer(){
        Timer timer = new Timer();
        timer.schedule(new TimerTask() {
            @Override
            public void run() {
                if (getPlaying()) {
                    try {
                        if(mSocketOutputStream != null) {
                            mSocketOutputStream.write(new byte[1]);
                            Log.i(TAG, "send heartbeat");
                        }
                    } catch (IOException e) {
                        Log.e(TAG, "send heartbeat err: " + e);
                    }
                    return;
                }
                try (DatagramSocket socket = new DatagramSocket(0)) {
                    socket.setBroadcast(true);
                    String message = HEAD + "@" + getListenPort() + "@" + getHttpPort();
                    byte[] data = message.getBytes();
                    for (int i = 58261; i < 58271; i++) {
                        socket.send(new DatagramPacket(data, data.length,
                                new InetSocketAddress(NetworkUtils.BROADCAST_ADDRESS, i)));
                    }
                    Log.i(TAG, "send broadcast " + socket.getLocalPort());
                } catch (Exception e) {
                    Log.e(TAG, "send broadcast error ", e);
                }
            }
        }, 0, 1000L * 10);
    }

    private int parseInt(@NonNull byte[] data) {
        return (data[0] & 0xFF) |
                ((data[1] & 0xFF) << 8) |
                ((data[2] & 0xFF) << 16) |
                ((data[3] & 0xFF) << 24);
    }

    public class TcpBinder extends Binder {
        TcpService getService() {
            return TcpService.this;
        }
    }

    public interface MessageListener {
        void onMessage();
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) {
        Log.i(TAG, "Service on bind");
        return binder;
    }
}
