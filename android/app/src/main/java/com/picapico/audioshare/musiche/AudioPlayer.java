package com.picapico.audioshare.musiche;

import android.annotation.SuppressLint;
import android.media.AudioManager;
import android.media.MediaPlayer;
import android.os.Handler;
import android.os.Looper;
import android.util.Log;

import com.picapico.audioshare.musiche.notification.NotificationActions;
import com.picapico.audioshare.musiche.notification.NotificationCallback;
import com.picapico.audioshare.musiche.notification.OnActionReceiveListener;

import org.json.JSONObject;

import java.util.List;
import java.util.Random;
import java.util.Timer;
import java.util.TimerTask;

public class AudioPlayer implements OnActionReceiveListener {
    private static final String TAG = "AudioShareAudioPlayer";
    enum LoopType {
        Single, Random, Order, Loop
    }
    private final Handler mHandler = new Handler(Looper.getMainLooper());
    private AudioManager mAudioManager = null;
    private int maxAudioVolume = 15;
    private LoopType loopType = LoopType.Loop;
    private boolean stopped = true;
    MediaPlayer mediaPlayer = new MediaPlayer();
    MusicPlayRequest mMusicPlayRequest;
    NotificationCallback mNotificationCallback;
    public interface PositionChangedListener {
        void onPositionChanged();
    }
    private PositionChangedListener positionChangedListener = null;

    public void setOnLoadSuccessListener(PositionChangedListener listener){
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
    public AudioPlayer(){
        mediaPlayer.setOnCompletionListener(this::onCompletion);
        mNotificationCallback = new NotificationCallback();
        mNotificationCallback.setOnActionReceiveListener(this);
        new Timer().schedule(onProgressTimer, 0, 500);
    }

    TimerTask onProgressTimer = new TimerTask() {
        @Override
        public void run() {
            if(mediaPlayer.isPlaying() && positionChangedListener != null){
                positionChangedListener.onPositionChanged();
                updateMediaMetadataPosition();
            }
        }
    };
    @Override
    public void onActionReceive(String action) {
        switch (action){
            case NotificationActions.ACTION_NEXT: next();break;
            case NotificationActions.ACTION_PREVIOUS: last();break;
            case NotificationActions.ACTION_PLAY_PAUSE: if(mediaPlayer.isPlaying()) pause();else play();break;
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
    private void onCompletion(MediaPlayer mp) {
        next();
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
                mediaPlayer.isPlaying(),
                mediaPlayer.getCurrentPosition(),
                mediaPlayer.getDuration()
                );
    }
    private void updateMediaMetadataPosition(){
        if(mediaMetaChangedListener == null || mMusicPlayRequest == null || mMusicPlayRequest.getMusic() == null) return;
        mediaMetaChangedListener.onMediaMetaChanged(
                mediaPlayer.isPlaying(),
                mediaPlayer.getDuration()
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
        try {
            mediaPlayer.start();
            stopped = false;
        } catch (Exception e) {
            Log.e(TAG, "play error", e);
            if(this.mMusicPlayRequest != null){
                play(this.mMusicPlayRequest.getMusic());
            }
        }
    }
    public void play(String url){
        if(url == null) return;
        try {
            mediaPlayer.reset();
            mediaPlayer.setDataSource(url);
            mediaPlayer.prepare();
            mediaPlayer.start();
            stopped = false;
            updateMediaMetadata();
        } catch (Exception e) {
            Log.e(TAG, "play url error," + url, e);
        }
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
        String url = musicItem.getUrl();
        if(url == null || url.isEmpty()){
            musicItem.getRemoteUrl(musicUrl -> {
                if(musicUrl == null){
                    this.mMusicPlayRequest.getPlaylist().remove(this.mMusicPlayRequest.getIndex());
                    next();
                }else {
                    play(musicUrl);
                }
            });
        }else {
            play(url);
            musicItem.setUrl(null);
        }
    }
    public void play(MusicPlayRequest musicPlayRequest){
        this.mMusicPlayRequest = musicPlayRequest;
        if(this.mMusicPlayRequest.getMusic() != null){
            play(this.mMusicPlayRequest.getMusic());
        }
    }
    public synchronized void setMusicPlayRequest(MusicPlayRequest musicPlayRequest){
        this.mMusicPlayRequest = musicPlayRequest;
    }
    public void pause(){
        mediaPlayer.pause();
    }
    public void setProgress(int percent){
        double percentDouble = percent*1.0/1000;
        percent = (int) (mediaPlayer.getDuration() * percentDouble);
        mediaPlayer.seekTo(percent);
    }
    public void setVolume(int percent){
        if(mAudioManager == null) return;
        double percentDouble = percent*1.0/100;
        percent = (int) (maxAudioVolume * percentDouble);
        int finalPercent = percent;
        mHandler.post(() -> {
            try {
                Log.i(TAG, "set music volume " + finalPercent);
                mAudioManager.setStreamVolume(
                        AudioManager.STREAM_MUSIC,
                        finalPercent,
                        AudioManager.FLAG_SHOW_UI);
            } catch (Exception ignored){
            }
        });
    }

    private int getVolume(){
        return (int)(mAudioManager.getStreamVolume(AudioManager.STREAM_MUSIC)*100/maxAudioVolume);
    }

    private int getProgress(){
        if(mediaPlayer.getDuration() == 0) return 0;
        return (int)(mediaPlayer.getCurrentPosition() * 1000 / mediaPlayer.getDuration());
    }

    @SuppressLint("DefaultLocale")
    private String parseMMSS(int milliseconds){
        if(milliseconds <= 0) return "00:00";
        int second = milliseconds / 1000;
        int minute = second / 60;
        second = second - minute * 60;
        return String.format("%02d", minute) + ":" + String.format("%02d", second);
    }

    public JSONObject getStatus() {
        JSONObject result = new JSONObject();
        JSONObject data = new JSONObject();
        try {
            data.put("volume", getVolume());
            data.put("currentTime", parseMMSS(mediaPlayer.getCurrentPosition()));
            data.put("totalTime", parseMMSS(mediaPlayer.getDuration()));
            data.put("playing", mediaPlayer.isPlaying());
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
