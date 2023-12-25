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

import androidx.annotation.NonNull;
import androidx.annotation.Nullable;

import com.phicomm.speaker.player.light.PlayerVisualizer;

import java.io.Closeable;
import java.io.DataInputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetAddress;
import java.net.ServerSocket;
import java.net.Socket;
import java.util.Timer;
import java.util.TimerTask;

public class TcpService extends Service {
    private static final String TAG = "AudioShareService";
    private static final String HEAD = "picapico-audio-share";
    private final IBinder binder = new TcpBinder();
    private MessageListener mListener;
    private LocalServerSocket localServerSocket = null;
    private ServerSocket serverSocket = null;
    private AudioManager mAudioManager = null;
    private int maxAudioVolume = 15;

    private boolean isWriting = false;

    @Override
    public void onCreate() {
        super.onCreate();
        Log.i(TAG, "Service on created");
        new Thread(this::startLocalServer).start();
        new Thread(this::startServer).start();
        startBroadcastTimer();
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
                if(mAudioManager != null){
                    volume = maxAudioVolume * volume / 100;
                    setVolume(volume);
                    mAudioManager.setStreamVolume(
                            AudioManager.STREAM_MUSIC,
                            volume,
                            AudioManager.FLAG_SHOW_UI);
                }
            } catch (IOException e) {
                Log.e(TAG, "read volume error: " + e);
            }
        }else if(command == 3) {
            PlayerVisualizer.updateTimeMillis();
        }
    }

    private void setVolume(int volume) {
        try {
            Log.i(TAG, "set music volume " + volume);
            mAudioManager.setStreamVolume(
                    AudioManager.STREAM_MUSIC,
                    volume,
                    AudioManager.FLAG_SHOW_UI);
        } catch (Exception ignored){
        }
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
            int bufferSizeInBytes = getMinBufferSize(sampleRate, channel, audioFormat);
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
        Log.i(TAG, "prepare start server");
        try {
            localServerSocket = new LocalServerSocket(HEAD);
            while (true){
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

    private void startServer(){
        Log.i(TAG, "prepare start server");
        try {
            int port = NetworkUtils.getFreePort();
            serverSocket = new ServerSocket(port);
            if(mListener != null){
                mListener.onMessage();
            }
            setListenPort(port);
            while (true){
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
        if(channelConfig == AudioFormat.CHANNEL_OUT_MONO) size /= 2;
        return Math.min(AudioTrack.getMinBufferSize(sampleRateInHz, channelConfig, audioFormat), size);
    }

    private void playAudio(int sampleRateInHz, int channelConfig, int audioFormat, int bufferSizeInBytes, Closeable closer, InputStream inputStream, OutputStream outputStream){
        if(getPlaying()) return;
        setPlaying(true);
        if(mListener != null){
            mListener.onMessage();
        }
        AudioTrack audioTrack = null;
        PlayerVisualizer playerVisualizer = null;
        try {
            audioTrack = new AudioTrack(
                    AudioManager.STREAM_MUSIC,
                    sampleRateInHz,
                    channelConfig,
                    audioFormat,
                    bufferSizeInBytes, AudioTrack.MODE_STREAM);
            setVolume(0);
            byte[] buffer = new byte[bufferSizeInBytes];
            int dataLength;
            audioTrack.play();
            playerVisualizer = new PlayerVisualizer(audioTrack.getAudioSessionId());
            Log.i(TAG, "play audio ready to read");
            outputStream.write(new byte[1]);
            outputStream.flush();
            DataInputStream stream = new DataInputStream(inputStream);
            while (true) {
                try {
                    stream.readFully(buffer, 0, 4);
                    dataLength = parseInt(buffer);
                    if(dataLength > buffer.length) {
                        buffer = new byte[dataLength];
                    }
                    stream.readFully(buffer, 0, dataLength);
                } catch (Exception e){
                    break;
                }
                if(getWriting()) continue;
                setWriting(true);
                AudioTrack finalAudioTrack = audioTrack;
                byte[] finalBuffer = buffer;
                int finalDataLength = dataLength;
                new Thread(() -> {
                    finalAudioTrack.write(finalBuffer, 0, finalDataLength);
                    setWriting(false);
                }).start();
            }
        } catch (Exception e) {
            Log.e(TAG, "play audio error: " + e);
            e.printStackTrace();
        } finally {
            if(playerVisualizer != null) {
                playerVisualizer.stop();
            }
            try {
                inputStream.close();
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
                if (getPlaying()) return;
                try (DatagramSocket socket = new DatagramSocket(0)) {
                    socket.setBroadcast(true);
                    InetAddress broadcastAddress = InetAddress.getByName(NetworkUtils.BROADCAST_ADDRESS);
                    String message = HEAD + "@" + getListenPort();
                    byte[] data = message.getBytes();
                    for (int i = 58261; i < 58271; i++) {
                        DatagramPacket packet = new DatagramPacket(
                                data, data.length, broadcastAddress, i);
                        socket.send(packet);
                    }
                    Log.i(TAG, "send broadcast " + socket.getLocalPort());
                } catch (Exception e) {
                    Log.e(TAG, "send broadcast error");
                    e.printStackTrace();
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
