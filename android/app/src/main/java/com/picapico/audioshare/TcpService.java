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

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.net.ServerSocket;
import java.net.Socket;

public class TcpService extends Service {
    private static final String TAG = "AudioShareService";
    private static final String HEAD = "picapico-audio-share";
    private static final int BUFFER_SIZE = 10240;
    private final IBinder binder = new TcpBinder();
    private MessageListener mListener;
    private LocalServerSocket localServerSocket = null;
    private ServerSocket serverSocket = null;
    private AudioManager mAudioManager = null;
    private int maxAudioVolume = 15;

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

    private byte readHead(InputStream stream) throws IOException {
        int bufferLength = HEAD.length();
        byte[] buffer = new byte[bufferLength];
        int offset = 0;
        int bytesRead = 0;
        while (offset < bufferLength &&
                (bytesRead = stream.read(buffer, offset, bufferLength - offset)) != -1){
            offset += bytesRead;
        }
        if(new String(buffer).equals(HEAD)){
            bytesRead = stream.read(buffer, 0, 1);
            if(bytesRead > 0 && buffer[0] > 0) {
                return buffer[0];
            }
        }
        return 0;
    }

    private void processControlStream(byte command, InputStream stream) {
        if(command == 2){
            try {
                DataInputStream inputStream = new DataInputStream(stream);
                byte[] volumeBuffer = new byte[4];
                inputStream.readFully(volumeBuffer, 0, 4);
                int volume = readInt(volumeBuffer);
                Log.i(TAG, "set music volume " + volume);
                if(mAudioManager != null){
                    volume = maxAudioVolume * volume / 100;
                    mAudioManager.setStreamVolume(
                            AudioManager.STREAM_MUSIC,
                            volume,
                            AudioManager.FLAG_SHOW_UI);
                }
            } catch (IOException e) {
                Log.e(TAG, "read volume error: " + e);
            }
        }
    }

    private void StartLocalServer(){
        Log.i(TAG, "prepare start server");
        try {
            localServerSocket = new LocalServerSocket(HEAD);
            while (true){
                LocalSocket clientSocket = localServerSocket.accept();
                InputStream stream = clientSocket.getInputStream();
                byte command = readHead(stream);
                Log.i(TAG, "client connected by local socket: " + command);
                if(command == 1 && !getPlaying()){
                    new Thread(() -> playAudio(clientSocket, stream)).start();
                }else {
                    processControlStream(command, stream);
                    try {
                        Log.i(TAG, "client connection close");
                        clientSocket.close();
                    } catch (IOException e) {
                        Log.e(TAG, "close client socket err: " + e);
                    }
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
                InputStream stream = clientSocket.getInputStream();
                byte command = readHead(stream);
                Log.i(TAG, "client connected by local socket: " + command);
                if(command == 1 && !getPlaying()){
                    new Thread(() -> playAudio(clientSocket, stream)).start();
                }else {
                    processControlStream(command, stream);
                    try {
                        Log.i(TAG, "client connection close");
                        clientSocket.close();
                    } catch (IOException e) {
                        Log.e(TAG, "close client socket err: " + e);
                    }
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

    public void setAudioManager(AudioManager audioManager){
        mAudioManager = audioManager;
        maxAudioVolume = mAudioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
    }

    private void playAudio(Closeable closer, InputStream stream){
        if(getPlaying()) return;
        setPlaying(true);
        if(mListener != null){
            mListener.onMessage();
        }
        DataInputStream inputStream = new DataInputStream(stream);
        AudioTrack audioTrack = null;
        try {
            byte[] sampleRateBuffer = new byte[4];
            inputStream.readFully(sampleRateBuffer, 0, 4);
            int sampleRate = readInt(sampleRateBuffer);
            int bufferSizeInBytes = AudioTrack.getMinBufferSize(
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT);
            audioTrack = new AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRate,
                    AudioFormat.CHANNEL_OUT_STEREO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    bufferSizeInBytes, AudioTrack.MODE_STREAM);
            audioTrack.play();
            byte[] buffer = new byte[BUFFER_SIZE];
            int bytesRead;
            audioTrack.play();
            while ((bytesRead = inputStream.read(buffer)) != -1) {
                if(bytesRead <= 0) continue;
                audioTrack.write(buffer, 0, bytesRead);
                audioTrack.flush();
                audioTrack.play();
            }
        } catch (Exception e) {
            Log.e(TAG, "play audio error: " + e);
            e.printStackTrace();
        } finally {
            try {
                stream.close();
            } catch (Exception e) {
                Log.e(TAG, "stop stream error: " + e);
            }
            try {
                closer.close();
            } catch (Exception e) {
                Log.e(TAG, "close audio socket error: " + e);
            }
            try {
                if(audioTrack != null) {
                    audioTrack.pause();
                    audioTrack.stop();
                    audioTrack.flush();
                    audioTrack.release();
                }
            } catch (Exception e) {
                Log.e(TAG, "stop audio error: " + e);
            }
        }
        setPlaying(false);
        if(mListener != null){
            mListener.onMessage();
        }
    }

    private int readInt(byte[] buffer) {
        return (buffer[0] & 0xFF) |
                ((buffer[1] & 0xFF) << 8) |
                ((buffer[2] & 0xFF) << 16) |
                ((buffer[3] & 0xFF) << 24);
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
