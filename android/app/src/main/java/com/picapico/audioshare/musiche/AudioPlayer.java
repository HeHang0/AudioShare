package com.picapico.audioshare.musiche;

import android.annotation.SuppressLint;
import android.content.Context;
import android.media.AudioManager;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import androidx.annotation.NonNull;
import androidx.media3.common.MediaItem;
import androidx.media3.common.PlaybackException;
import androidx.media3.common.Player;
import androidx.media3.common.Timeline;
import androidx.media3.exoplayer.ExoPlayer;

import com.picapico.audioshare.musiche.notification.NotificationActions;
import com.picapico.audioshare.musiche.notification.NotificationCallback;
import com.picapico.audioshare.musiche.notification.OnActionReceiveListener;

import org.json.JSONObject;

import java.util.List;
import java.util.Random;
import java.util.Timer;
import java.util.TimerTask;

public class AudioPlayer implements OnActionReceiveListener, Player.Listener {
    private static final String TAG = "AudioShareAudioPlayer";
    private final Handler handler = new Handler(Looper.getMainLooper());
    enum LoopType {
        Single, Random, Order, Loop
    }

    private AudioManager mAudioManager = null;
    private int maxAudioVolume = 15;
    private LoopType loopType = LoopType.Loop;
    private MusicItem.Quality quality = MusicItem.Quality.PQ;
    private boolean stopped = true;
    private boolean playing = false;
    private int position = 0;
    private int duration = 0;
    private final ExoPlayer mediaPlayer;
    private MusicPlayRequest mMusicPlayRequest;
    private final NotificationCallback mNotificationCallback;
    private Timer progressTimer;
    public interface OnPositionChangedListener {
        void onPositionChanged();
    }
    private OnPositionChangedListener positionChangedListener = null;

    public void setOnLoadSuccessListener(OnPositionChangedListener listener){
        positionChangedListener = listener;
    }
    public interface MediaMetaChangedListener {
        void onMediaMetaChanged(boolean playing, int position);
        void onMediaMetaChanged(String title, String artist, String album, String artwork, boolean lover, boolean playing, int position, int duration);
    }
    private MediaMetaChangedListener mediaMetaChangedListener = null;

    public void setMediaMetaChangedListener(MediaMetaChangedListener listener){
        mediaMetaChangedListener = listener;
    }

    public AudioPlayer(Context context){
        mediaPlayer = new ExoPlayer.Builder(context).build();
        mediaPlayer.addListener(this);
        mNotificationCallback = new NotificationCallback();
        mNotificationCallback.setOnActionReceiveListener(this);
    }

    @Override
    public void onTimelineChanged(@NonNull Timeline timeline, int reason) {
        duration = (int) mediaPlayer.getDuration();
        position = (int) mediaPlayer.getCurrentPosition();
        Log.i(TAG, "onTimelineChanged: " + timeline + " " + reason);
    }
    @Override
    public void onIsLoadingChanged(boolean isLoading) {
        Log.i(TAG, "onIsLoadingChanged: " + isLoading);
    }
    @Override
    public void onPlaybackStateChanged(int playbackState) {
        Log.i(TAG, "onPlayerStateChanged: " + playbackState);
        if(playbackState == Player.STATE_ENDED){
            next();
        }
    }
    @Override
    public void onPlayWhenReadyChanged(boolean playWhenReady, int reason) {
        Log.i(TAG, "onPlayWhenReadyChanged: " + playWhenReady);
    }
    @Override
    public void onIsPlayingChanged(boolean isPlaying) {
        Log.i(TAG, "onIsPlayingChanged: " + isPlaying);
        playing = isPlaying;
        if(positionChangedListener != null){
            positionChangedListener.onPositionChanged();
        }
        updateMediaMetadataPosition();
        if(playing && progressTimer == null){
            progressTimer = new Timer();
            progressTimer.schedule(getProgressTimerTask(), 0, 500);
        }else if(!playing && progressTimer != null){
            try {
                progressTimer.cancel();
                progressTimer = null;
            }catch (Exception e){
                Log.e(TAG, "cancel timer error: ", e);
            }
        }
    }
    @Override
    public void onPlayerError(@NonNull PlaybackException error) {
        Log.e(TAG, "onPlayerError: ", error);
    }

    private TimerTask getProgressTimerTask(){
        return new TimerTask() {
            @Override
            public void run() {
                if(!playing || positionChangedListener == null) return;
                handler.post(() -> {
                    position = (int) mediaPlayer.getCurrentPosition();
                    positionChangedListener.onPositionChanged();
                    updateMediaMetadataPosition();
                });
            }
        };
    }
    @Override
    public void onActionReceive(String action) {
        switch (action){
            case NotificationActions.ACTION_NEXT: next();break;
            case NotificationActions.ACTION_PREVIOUS: last();break;
            case NotificationActions.ACTION_PLAY_PAUSE: if(playing) pause();else play();break;
            case NotificationActions.ACTION_PLAY: play();break;
            case NotificationActions.ACTION_PAUSE: pause();break;
            case NotificationActions.ACTION_LOVER: break;
        }
    }

    @Override
    public void onActionReceive(long pos) {
        setProgress((int) pos);
    }

    public NotificationCallback getNotificationCallback(){
        return mNotificationCallback;
    }

    public void setAudioManager(AudioManager audioManager){
        mAudioManager = audioManager;
        maxAudioVolume = mAudioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
    }
    public void setLoopType(LoopType loopType) {
        this.loopType = loopType;
    }
    public void setQuality(MusicItem.Quality quality) {
        this.quality = quality;
    }
    private void updateMediaMetadata(){
        if(mediaMetaChangedListener == null || mMusicPlayRequest == null || mMusicPlayRequest.getMusic() == null) return;
        MusicItem musicItem = mMusicPlayRequest.getMusic();
        mediaMetaChangedListener.onMediaMetaChanged(
                musicItem.getName(),
                musicItem.getSinger(),
                musicItem.getAlbum(),
                musicItem.getImage(),
                false,
                playing,
                position,
                duration
        );
    }
    private void updateMediaMetadataPosition(){
        if(mediaMetaChangedListener == null || mMusicPlayRequest == null || mMusicPlayRequest.getMusic() == null) return;
        mediaMetaChangedListener.onMediaMetaChanged(
                playing,
                duration
        );
    }
    public void last(){
        if(this.mMusicPlayRequest == null) return;
        if(lastIndex == 0 && this.mMusicPlayRequest.getIndex() == 0){
            lastIndex = this.mMusicPlayRequest.getPlaylist().size() - 1;
        }
        play(lastIndex);
    }
    public void next(){
        if(mMusicPlayRequest == null || mMusicPlayRequest.getPlaylist().size() == 0 || loopType == LoopType.Single){
            try {
                play();
            }catch (Exception ignore) {}
            return;
        }
        int index = mMusicPlayRequest.getIndex()+1;
        switch (loopType){
            case Loop:
                if(index >= mMusicPlayRequest.getPlaylist().size()){
                    index = 0;
                }
                break;
            case Order:
                if(index >= mMusicPlayRequest.getPlaylist().size()){
                    try {
                        pause();
                    }catch (Exception ignore) {}
                    return;
                }
                break;
            case Random:
                index = new Random().nextInt(mMusicPlayRequest.getPlaylist().size());
                break;
        }
        play(mMusicPlayRequest.getPlaylist().get(index));
    }
    public void play(){
        handler.post(() -> {
            boolean needPlay = false;
            try {
                mediaPlayer.play();
                stopped = false;
            } catch (Exception e) {
                Log.e(TAG, "play error", e);
                needPlay = true;
            }
            if(!needPlay && mediaPlayer.getDuration() > 0){
                return;
            }
            if(this.mMusicPlayRequest != null){
                play(this.mMusicPlayRequest.getMusic());
            }
        });
    }
    public void play(String url){
        if(url == null || url.isEmpty()) {
            pause();
            return;
        }
        play(Uri.parse(url));
    }
    public void play(Uri uri){
        if(uri == null) {
            pause();
            return;
        }
        handler.post(() -> {
            try {
                mediaPlayer.stop();
                Log.i(TAG, "play url " + uri);
                mediaPlayer.setMediaItem(MediaItem.fromUri(uri));
                mediaPlayer.prepare();
                mediaPlayer.play();
                stopped = false;
                updateMediaMetadata();
            } catch (Exception e) {
                pause();
                Log.e(TAG, "play url error," + uri, e);
            }
        });
    }
    private int lastIndex = 0;
    public void play(int index){
        if(this.mMusicPlayRequest == null) return;
        play(this.mMusicPlayRequest.getPlaylist().get(index));
    }
    public void play(MusicItem musicItem){
        if(musicItem == null) return;
        if(this.mMusicPlayRequest != null){
            lastIndex = this.mMusicPlayRequest.getIndex();
            this.mMusicPlayRequest.setMusic(musicItem);
            List<MusicItem> playlist = this.mMusicPlayRequest.getPlaylist();
            int index = playlist.indexOf(musicItem);
            if(index >= 0) this.mMusicPlayRequest.setIndex(index);
            else {
                playlist.add(musicItem);
                this.mMusicPlayRequest.setIndex(playlist.size() - 1);
            }
        }
        musicItem.getMusicUrl(quality, musicUrl -> {
            if(musicUrl == null){
                this.mMusicPlayRequest.getPlaylist().remove(this.mMusicPlayRequest.getIndex());
                next();
            }else {
                play(musicUrl);
            }
        });
    }
    public void play(MusicPlayRequest musicPlayRequest){
        this.mMusicPlayRequest = musicPlayRequest;
        if(this.mMusicPlayRequest.getMusic() != null){
            play(this.mMusicPlayRequest.getMusic());
        }
    }
    public void setMusicPlayRequest(MusicPlayRequest musicPlayRequest){
        this.mMusicPlayRequest = musicPlayRequest;
    }
    public void pause(){
        handler.post(() -> {
            try{
                mediaPlayer.pause();
            }catch (Exception ignore){}
        });
    }
    public void setProgress(int percent){
        double percentDouble = percent*1.0/1000;
        percent = (int) (duration * percentDouble);
        if(percent > 0 && percent <= duration){
            int finalPercent = percent;
            handler.post(() -> mediaPlayer.seekTo(finalPercent));
        }
    }
    public void setVolume(int percent){
        if(mAudioManager == null) return;
        double percentDouble = percent*1.0/100;
        percent = (int) (maxAudioVolume * percentDouble);
        try {
            mAudioManager.setStreamVolume(
                    AudioManager.STREAM_MUSIC,
                    percent,
                    AudioManager.FLAG_SHOW_UI);
        } catch (Exception e){
            Log.e(TAG, "set music volume error", e);
        }
    }

    private int getVolume(){
        if(mAudioManager != null) {
            return (int)(mAudioManager.getStreamVolume(AudioManager.STREAM_MUSIC)*100/maxAudioVolume);
        }else {
            return 100;
        }
    }

    private int getProgress(){
        if(duration == 0) return 0;
        return position * 1000 / duration;
    }

    @SuppressLint("DefaultLocale")
    private String parseMMSS(long milliseconds){
        if(milliseconds <= 0) return "00:00";
        long second = milliseconds / 1000;
        long minute = second / 60;
        second = second - minute * 60;
        return String.format("%02d", minute) + ":" + String.format("%02d", second);
    }

    public JSONObject getStatus() {
        JSONObject result = new JSONObject();
        JSONObject data = new JSONObject();
        try {
            data.put("volume", getVolume());
            data.put("currentTime", parseMMSS(position));
            data.put("totalTime", parseMMSS(duration));
            data.put("playing", playing);
            data.put("stopped", stopped);
            data.put("progress", getProgress());
            if(mMusicPlayRequest != null){
                MusicItem musicItem = mMusicPlayRequest.getMusic();
                if(musicItem != null){
                    data.put("id", musicItem.getId());
                    data.put("type", musicItem.getType());
                }
            }
            result.put("data", data);
            result.put("type", "status");
        }catch (Exception e){
            Log.e(TAG, "set status error", e);
        }
        return result;
    }
}
