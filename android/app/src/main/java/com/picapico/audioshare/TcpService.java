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
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.ServerSocket;
import java.net.Socket;

public class TcpService extends Service {
    private static final String TAG = "AudioShareService";
    private static final String HEAD = "picapico-audio-share";
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
        int bytesRead;
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

    private void processSocketClient(Closeable socket) throws IOException {
        InputStream stream;
        boolean isLocal = false;
        if (socket instanceof LocalSocket){
            stream = ((LocalSocket)socket).getInputStream();
            isLocal = true;
        }else if (socket instanceof Socket){
            stream = ((Socket)socket).getInputStream();
        }else {
            return;
        }
        byte command = readHead(stream);
        Log.i(TAG, "client connected: " + command);
        if(command == 1 && !getPlaying()){
            int sampleRate = readInt(stream);
            int channel = readInt(stream);
            int audioFormat = AudioFormat.ENCODING_PCM_16BIT;
            int bufferSizeInBytes = getMinBufferSize(sampleRate, channel, audioFormat);
            OutputStream outputStream;
            if(isLocal){
                ((LocalSocket)socket).setReceiveBufferSize(bufferSizeInBytes);
                outputStream = ((LocalSocket)socket).getOutputStream();
            }else {
                ((Socket)socket).setReceiveBufferSize(bufferSizeInBytes);
                outputStream = ((Socket)socket).getOutputStream();
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
            try {
                Log.i(TAG, "client connection close");
                socket.close();
            } catch (IOException e) {
                Log.e(TAG, "close client err: " + e);
            }
        }
    }

    private void StartLocalServer(){
        Log.i(TAG, "prepare start server");
        try {
            localServerSocket = new LocalServerSocket(HEAD);
            while (true){
                LocalSocket clientSocket = localServerSocket.accept();
                processSocketClient(clientSocket);
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
                clientSocket.setTcpNoDelay(true);
                processSocketClient(clientSocket);
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

    private int getMinBufferSize(int sampleRateInHz, int channelConfig, int audioFormat){
        int size = 40960;
        switch (sampleRateInHz){
            case 192000: size = 30752; break;
            case 176400: size = 28256; break;
            case 96000: size = 15392; break;
            case 48000: size = 7696; break;
            case 44100: size = 7088; break;
        }
        return Math.min(AudioTrack.getMinBufferSize(sampleRateInHz, channelConfig, audioFormat), size);
    }

    private void playAudio(int sampleRateInHz, int channelConfig, int audioFormat, int bufferSizeInBytes, Closeable closer, InputStream stream, OutputStream outputStream){
        if(getPlaying()) return;
        setPlaying(true);
        if(mListener != null){
            mListener.onMessage();
        }
        AudioTrack audioTrack = null;
        try {
            audioTrack = new AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRateInHz,
                    channelConfig,
                    audioFormat,
                    bufferSizeInBytes, AudioTrack.MODE_STREAM);
            byte[] buffer = new byte[bufferSizeInBytes];
            int bytesRead;
            audioTrack.play();
            Log.i(TAG, "play audio ready to read");
            outputStream.write(new byte[1]);
            outputStream.flush();
            while ((bytesRead = stream.read(buffer)) != -1) {
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
                outputStream.close();
            } catch (Exception e) {
                Log.e(TAG, "stop output stream error: " + e);
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

    private int parseInt(byte[] buffer) {
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
