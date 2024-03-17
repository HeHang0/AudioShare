package com.picapico.audioshare.musiche.player;

import android.content.Context;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.annotation.OptIn;
import androidx.media3.common.AudioAttributes;
import androidx.media3.common.C;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.Timeline;
import androidx.media3.common.Tracks;
import androidx.media3.common.util.UnstableApi;
import androidx.media3.exoplayer.DefaultRenderersFactory;
import androidx.media3.exoplayer.SeekParameters;

public class ExoPlayer implements IMediaPlayer, Player.Listener {
    private static final String TAG = "AudioShareExoPlayer";
    private final androidx.media3.exoplayer.ExoPlayer mediaPlayer;
    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable updateProgressAction;
    private Listener mediaChangedListener;
    private boolean playing = false;
    private int position = 0;
    private int duration = 0;
    @OptIn(markerClass = UnstableApi.class) public ExoPlayer(Context context){
        AudioAttributes audioAttributes = new AudioAttributes.Builder()
                .setUsage(C.USAGE_MEDIA)
                .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                .build();
        androidx.media3.exoplayer.ExoPlayer.Builder builder =
                new androidx.media3.exoplayer.ExoPlayer.Builder(context);
        DefaultRenderersFactory factory = new DefaultRenderersFactory(context.getApplicationContext())
                .setExtensionRendererMode(DefaultRenderersFactory.EXTENSION_RENDERER_MODE_ON);
        builder.setRenderersFactory(factory);
        mediaPlayer = builder.build();
        mediaPlayer.setAudioAttributes(audioAttributes, true);
        mediaPlayer.addListener(this);
        mediaPlayer.setPlayWhenReady(true);
        updateProgressAction = this::updateProgress;
    }

    public void setMediaChangedListener(Listener listener){
        this.mediaChangedListener = listener;
    }

    @Override
    public void play() {
        if(playing) return;
        handler.post(() -> {
            try {
                mediaPlayer.play();
                playing = true;
            } catch (Exception e) {
                Log.e(TAG, "play error", e);
            }
        });
    }

    @Override
    public void play(String url) {
        handler.post(() -> {
            try {
                mediaPlayer.stop();
                Log.i(TAG, "play url " + url);
                mediaPlayer.setMediaItem(MediaItem.fromUri(url));
                mediaPlayer.prepare();
                mediaPlayer.play();
                playing = true;
            } catch (Exception e) {
                pause();
                Log.e(TAG, "play url error," + url, e);
            }
        });
    }

    @Override
    public void pause() {
        playing = false;
        handler.post(() -> {
            try{
                if(!mediaPlayer.isPlaying()) return;
                mediaPlayer.pause();
            }catch (Exception ignore){}
        });
    }

    @Override
    public void seekTo(int position) {
        handler.post(() -> mediaPlayer.seekTo(position));
    }

    @OptIn(markerClass = UnstableApi.class) @Override
    public void setSeekDiscontinuity(boolean discontinuity){
        handler.post(() -> mediaPlayer.setSeekParameters(
                discontinuity ? SeekParameters.NEXT_SYNC : SeekParameters.DEFAULT));
    }

    @Override
    public void setVolume(int volume) {
        handler.post(() -> mediaPlayer.setVolume(volume));
    }

    @Override
    public int getDuration() {
        return duration;
    }

    @Override
    public int getPosition() {
        return position;
    }

    @Override
    public void getRealtimePosition(RealtimePositionCallback callback) {
        handler.post(() -> {
            position = (int) mediaPlayer.getCurrentPosition();
            callback.onPosition(position);
        });
    }

    @Override
    public boolean isPlaying() {
        return playing;
    }

    //region ExoPlayer listener
    @Override
    public void onTimelineChanged(@NonNull Timeline timeline, int reason) {
        duration = (int) mediaPlayer.getDuration();
        position = (int) mediaPlayer.getCurrentPosition();
        if(mediaChangedListener != null && duration > 1){
            mediaChangedListener.onDurationChanged(position, duration);
        }
    }
    @Override
    public void onPlaybackStateChanged(int playbackState) {
        Log.i(TAG, "onPlayerStateChanged: " + playbackState);
        if(mediaChangedListener != null && playbackState == Player.STATE_ENDED){
            mediaChangedListener.onPlaybackStateChanged(Listener.STATE_ENDED);
        }else if(playbackState == Player.STATE_READY) updateProgress();
    }
    @Override
    public void onDeviceVolumeChanged(int volume, boolean muted){
        if(mediaChangedListener != null){
            mediaChangedListener.onDeviceVolumeChanged(muted ? 0 : volume);
        }
    }
    @Override
    public void onIsPlayingChanged(boolean isPlaying) {
        playing = isPlaying;
        if(playing) updateProgress();
        if(mediaChangedListener != null){
            mediaChangedListener.onPlayingChanged(isPlaying);
        }
    }
    @Override
    public void onPlayerError(@NonNull PlaybackException error) {
        Log.e(TAG, "onPlayerError: ", error);
    }
    @Override
    @SuppressWarnings("ReferenceEquality")
    public void onTracksChanged(Tracks tracks) {
        if (tracks.containsType(C.TRACK_TYPE_AUDIO)
                && !tracks.isTypeSupported(C.TRACK_TYPE_AUDIO, /* allowExceedsCapabilities= */ true)) {
            Log.w(TAG, "Media includes audio tracks, but none are playable by this device");
        }
    }
    private void updateProgress(){
        handler.removeCallbacks(updateProgressAction);
        position = (int) mediaPlayer.getCurrentPosition();
        playing = mediaPlayer.isPlaying();
        if(mediaChangedListener != null){
            mediaChangedListener.onPositionChanged(playing, position, duration);
        }
        if(playing){
            handler.postDelayed(updateProgressAction, 500);
        }
    }
    //endregion
}
