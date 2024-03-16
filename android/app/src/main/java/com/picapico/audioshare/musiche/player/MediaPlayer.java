package com.picapico.audioshare.musiche.player;

import android.media.AudioAttributes;
import android.media.MediaTimestamp;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.NonNull;

public class MediaPlayer implements IMediaPlayer {
    private static final String TAG = "AudioShareMediaPlayer";
    private final android.media.MediaPlayer mediaPlayer;
    private Listener mediaChangedListener;
    private boolean stopped = true;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable updateProgressAction;
    public MediaPlayer(){
        AudioAttributes audioAttributes = new AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_MEDIA)
                .setContentType(AudioAttributes.CONTENT_TYPE_MUSIC)
                .build();
        mediaPlayer = new android.media.MediaPlayer();
        mediaPlayer.setAudioAttributes(audioAttributes);
        mediaPlayer.setOnCompletionListener(this::onPlaybackStateCompleted);
        mediaPlayer.setOnErrorListener(this::onPlayerError);
        mediaPlayer.setOnPreparedListener(this::onPrepared);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            mediaPlayer.setOnMediaTimeDiscontinuityListener(this::onMediaTimeDiscontinuity);
        }
        updateProgressAction = this::updateProgress;
    }
    @Override
    public void setMediaChangedListener(Listener listener){
        this.mediaChangedListener = listener;
    }

    @Override
    public void play() {
        if(mediaPlayer.isPlaying()) return;
        try {
            mediaPlayer.start();
            handler.post(this::updateProgress);
            if(mediaChangedListener != null){
                mediaChangedListener.onPlayingChanged(mediaPlayer.isPlaying());
            }
        } catch (Exception e) {
            Log.e(TAG, "play error", e);
        }
    }

    @Override
    public void play(String url) {
        try {
            mediaPlayer.reset();
            Log.i(TAG, "play url " + url);
            mediaPlayer.setDataSource(url);
            mediaPlayer.prepareAsync();
        } catch (Exception e) {
            pause();
            Log.e(TAG, "play url error," + url, e);
        }
    }

    @Override
    public void pause() {
        try{
            if(!mediaPlayer.isPlaying()) return;
            mediaPlayer.pause();
        }catch (Exception ignore){}
    }

    @Override
    public void seekTo(int position) {
        mediaPlayer.seekTo(position);
    }

    @Override
    public void setVolume(int volume) {
        mediaPlayer.setVolume(volume, volume);
    }

    @Override
    public int getDuration() {
        if(stopped) return 0;
        return mediaPlayer.getDuration();
    }

    @Override
    public int getPosition() {
        return mediaPlayer.getCurrentPosition();
    }

    @Override
    public void getRealtimePosition(RealtimePositionCallback callback) {
        callback.onPosition(mediaPlayer.getCurrentPosition());
    }

    @Override
    public boolean isPlaying() {
        return mediaPlayer.isPlaying();
    }
    public void onMediaTimeDiscontinuity(@NonNull android.media.MediaPlayer mp, @NonNull MediaTimestamp mts) {
        Log.i(TAG, "onMediaTimeDiscontinuity");
        this.onPrepared(mp);
    }
    public void onPrepared(android.media.MediaPlayer mp) {
        Log.i(TAG, "onPrepared");
        mediaPlayer.start();
        stopped = false;
        int duration = mediaPlayer.getDuration();
        int position = mediaPlayer.getCurrentPosition();
        if(mediaChangedListener != null && duration > 1){
            mediaChangedListener.onDurationChanged(position, duration);
            mediaChangedListener.onPlayingChanged(mediaPlayer.isPlaying());
        }
        handler.post(this::updateProgress);
    }
    public void onPlaybackStateCompleted(android.media.MediaPlayer mp) {
        Log.i(TAG, "onPlaybackStateCompleted");
        if(mediaChangedListener != null){
            mediaChangedListener.onPlaybackStateChanged(Listener.STATE_ENDED);
        }
        handler.post(this::updateProgress);
    }
    public boolean onPlayerError(android.media.MediaPlayer mp, int what, int extra) {
        Log.e(TAG, "onPlayerError: " + what + ", " + extra);
        return true;
    }
    private void updateProgress(){
        handler.removeCallbacks(updateProgressAction);
        if(mediaChangedListener != null){
            mediaChangedListener.onPositionChanged(mediaPlayer.isPlaying(), mediaPlayer.getCurrentPosition(), mediaPlayer.getDuration());
        }
        if(mediaPlayer.isPlaying()){
            handler.postDelayed(updateProgressAction, 500);
        }
    }
    //endregion
}