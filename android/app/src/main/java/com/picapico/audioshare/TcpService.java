package com.picapico.audioshare;

import android.app.Service;
import android.content.Intent;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioTrack;
import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Binder;
import android.os.IBinder;
import android.util.Log;

import androidx.annotation.Nullable;

import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.ServerSocket;
import java.net.Socket;

public class TcpService extends Service {
    private static final String TAG = "AudioShareService";
    private final IBinder binder = new TcpBinder();
    private MessageListener mListener;
    private LocalServerSocket localServerSocket = null;
    private ServerSocket serverSocket = null;

    @Override
    public void onCreate() {
        super.onCreate();
        Log.i(TAG, "Service on created");
        new Thread(this::StartLocalServer).start();
        new Thread(this::StartServer).start();
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        Log.i(TAG, "Service on start command");
        return START_STICKY;
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
        Log.i(TAG, "Service on destroy");
        try {
            if(localServerSocket != null){
                localServerSocket.close();
            }
        } catch (IOException e) {
            Log.e(TAG, "close local server error: " + e);
        }
        try {
            if(serverSocket != null){
                serverSocket.close();
            }
        } catch (IOException e) {
            Log.e(TAG, "close tcp server error: " + e);
        }
    }

    private void StartLocalServer(){
        Log.i(TAG, "prepare start server");
        try {
            localServerSocket = new LocalServerSocket("picapico-audio-share");
            while (true){
                LocalSocket clientSocket = localServerSocket.accept();
                Log.i(TAG, "client connected by local socket");
                playAudio(clientSocket.getInputStream());
                try {
                    Log.i(TAG, "client connection close");
                    clientSocket.close();
                } catch (IOException e) {
                    Log.e(TAG, "close client socket err: " + e);
                }
            }
        } catch (IOException | RuntimeException e) {
            Log.e(TAG, e.toString());
        } finally {
            try {
                if(localServerSocket != null) {
                    localServerSocket.close();
                }
            } catch (IOException e) {
                Log.e(TAG, "start local server error: " + e);
            }
        }
    }

    private void StartServer(){
        Log.i(TAG, "prepare start server");
        try {
            int port = 8088;
            for (; port < 65535; port++) {
                if(!isPortBusy(port)){
                    break;
                }
            }
            serverSocket = new ServerSocket(port);
            if(mListener != null){
                mListener.onMessage();
            }
            setListenPort(port);
            while (true){
                Socket clientSocket = serverSocket.accept();
                Log.i(TAG, "client connected by local socket");
                playAudio(clientSocket.getInputStream());
                try {
                    Log.i(TAG, "client connection close");
                    clientSocket.close();
                } catch (IOException e) {
                    Log.e(TAG, "close client socket err: " + e);
                }
            }
        } catch (IOException | RuntimeException e) {
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

    private boolean isPortBusy(int port){
        try {
            new ServerSocket(port).close();
            return false;
        } catch (Exception ignore) {
            return true;
        }
    }

    private boolean isPlaying = false;

    private synchronized void setPlaying(boolean playing) {
        isPlaying = playing;
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

    public void setMessageListener(MessageListener listener){
        mListener = listener;
    }

    private void playAudio(InputStream stream){
        if(getPlaying()) return;
        setPlaying(true);
        if(mListener != null){
            mListener.onMessage();
        }
        DataInputStream inputStream = new DataInputStream(stream);
        AudioTrack audioTrack = null;
        try {
            int sampleRate = inputStream.readInt();
            audioTrack = new AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    1024, AudioTrack.MODE_STREAM);
            audioTrack.play();
            byte[] buffer = new byte[512];
            int bytesRead;
            while ((bytesRead = inputStream.read(buffer)) != -1) {
                if(bytesRead <= 0) continue;
                audioTrack.write(buffer, 0, bytesRead);
                audioTrack.flush();
                audioTrack.play();
            }
        } catch (IOException e) {
            Log.e(TAG, "play audio error: " + e);
        } finally {
            if(audioTrack != null) audioTrack.stop();
        }
        setPlaying(false);
        if(mListener != null){
            mListener.onMessage();
        }
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
