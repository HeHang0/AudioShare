package com.picapico.audioshare.musiche.player;

import android.annotation.SuppressLint;
import android.content.Context;
import android.media.AudioManager;
import android.util.Log;

import androidx.annotation.IntDef;

import com.phicomm.speaker.player.light.PlayerVisualizer;
import com.picapico.audioshare.musiche.MusicItem;
import com.picapico.audioshare.musiche.MusicPlayRequest;
import com.picapico.audioshare.musiche.notification.NotificationActions;
import com.picapico.audioshare.musiche.notification.NotificationCallback;
import com.picapico.audioshare.musiche.notification.OnActionReceiveListener;

import org.json.JSONObject;

import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Random;

public class AudioPlayer implements OnActionReceiveListener, IMediaPlayer.Listener {
    private static final String TAG = "AudioShareAudioPlayer";

    //region Type definition
    public enum LoopType {
        Single, Random, Order, Loop
    }

    public static final int ChannelTypeNone = -1;
    public static final int ChannelTypeStereo = 0;
    public static final int ChannelTypeLeft = 1;
    public static final int ChannelTypeRight = 2;
    @IntDef(
            value = {
                    ChannelTypeNone,
                    ChannelTypeStereo,
                    ChannelTypeLeft,
                    ChannelTypeRight
            }
    )
    public @interface ChannelType {}
    public interface OnChangedListener {
        void onPositionChanged();
        void onPositionChanged(long position);
        void onPaused();
        void onVolumeChanged(int volume);
        void onPlaying(boolean remote, String url, MusicItem music, int volume);
    }
    public interface OnMediaMetaChangedListener {
        void onMediaMetaChanged(boolean playing, int position);
        void onMediaMetaChanged(String title, String artist, String album, String artwork, boolean lover, boolean playing, int position, int duration);
    }
    //endregion
    //region Private field
    private @ChannelType int mChannel = ChannelTypeStereo;

    private AudioManager mAudioManager = null;
    private int maxAudioVolume = 15;
    private LoopType loopType = LoopType.Loop;
    private MusicItem.Quality quality = MusicItem.Quality.PQ;
    private final IMediaPlayer mediaPlayer;
    private MusicPlayRequest mMusicPlayRequest;
    private final NotificationCallback mNotificationCallback;
    private OnChangedListener changedListener = null;
    private OnMediaMetaChangedListener mediaMetaChangedListener = null;
    private boolean remote = false;
    private int lastIndex = 0;
    private int volume = 100;
    private boolean stopped = true;
    private String url = "";
    //endregion
    public AudioPlayer(Context context){
        mediaPlayer = new ExoPlayer(context);
        mediaPlayer.setMediaChangedListener(this);
        mNotificationCallback = new NotificationCallback();
        mNotificationCallback.setOnActionReceiveListener(this);
    }
    //region Public method
    public void setChannel(@ChannelType int channel) {
        if(this.mChannel == channel) return;
        this.mChannel = channel;
        mediaPlayer.setVolume(channel == ChannelTypeNone ? 0 : 1);
    }
    public int getChannel() {
        return mChannel;
    }
    public boolean isPlaying(){
        return mediaPlayer.isPlaying();
    }

    public void setOnLoadSuccessListener(OnChangedListener listener){
        changedListener = listener;
    }

    public void setMediaMetaChangedListener(OnMediaMetaChangedListener listener){
        mediaMetaChangedListener = listener;
    }

    public NotificationCallback getNotificationCallback(){
        return mNotificationCallback;
    }

    public void setAudioManager(AudioManager audioManager){
        mAudioManager = audioManager;
        maxAudioVolume = mAudioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC);
        volume = mAudioManager.getStreamVolume(AudioManager.STREAM_MUSIC)*100/maxAudioVolume;
    }
    public void setLoopType(LoopType loopType) {
        this.loopType = loopType;
    }
    public void setQuality(MusicItem.Quality quality) {
        this.quality = quality;
    }
    public int getVolume(){
        return volume;
    }

    public MusicItem getCurrentMusic(){
        if(mMusicPlayRequest != null) return mMusicPlayRequest.getMusic();
        return null;
    }

    public String getUrl(){
        return url;
    }

    public void getPosition(){
        mediaPlayer.getRealtimePosition(position -> changedListener.onPositionChanged(position));
    }
    public JSONObject getStatus() {
        Map<String, Object> result = new HashMap<>();
        Map<String, Object> data = new HashMap<>();
        int duration = mediaPlayer.getDuration();
        int position = mediaPlayer.getPosition();
        int progress = duration <= 0 || position <= 0 ? 0 : (position * 1000 / duration);
        try {
            data.put("volume", getVolume());
            data.put("currentTime", parseMMSS(mediaPlayer.getPosition()));
            data.put("totalTime", parseMMSS(mediaPlayer.getDuration()));
            data.put("playing", mediaPlayer.isPlaying());
            data.put("stopped", stopped);
            data.put("progress", progress);
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
        return new JSONObject(result);
    }
    //endregion

    //region IMediaPlayer listener
    @Override
    public void onDurationChanged(int position, int duration) {
        updateMediaMetadata();
        if(changedListener != null){
            changedListener.onPositionChanged();
        }
    }

    private long lastSendTime = 0;
    @Override
    public void onPositionChanged(boolean playing, int position, int duration) {
        updateMediaMetadataPosition();
        if(changedListener != null){
            changedListener.onPositionChanged();
            if(!remote) return;
            long now = System.currentTimeMillis();
            if(now - lastSendTime > 1500) {
                changedListener.onPlaying(true, null, null, 0);
                lastSendTime = now;
            }
        }
    }
    @Override
    public void onPlaybackStateChanged(int playbackState) {
        Log.i(TAG, "onPlayerStateChanged: " + playbackState);
        if(playbackState == IMediaPlayer.Listener.STATE_ENDED && !remote){
            next();
        }
    }
    private int playingChangedDelay = 0;
    private long lastStatusTime = 0;
    @Override
    public void onPlayingChanged(boolean playing) {
        if(playing){
            stopped = false;
            playingChangedDelay = (int) (System.currentTimeMillis() - lastStatusTime);
            PlayerVisualizer.start(mediaPlayer.getAudioSessionId());
        }else {
            lastStatusTime = System.currentTimeMillis();
            PlayerVisualizer.stop();
        }
        if(changedListener != null){
            if(playing && mMusicPlayRequest != null && url != null && !url.isEmpty()) {
                changedListener.onPlaying(remote, url, mMusicPlayRequest.getMusic(), volume);
            }
        }

        updateMediaMetadata();
    }
    //endregion

    //region Notification listener
    @Override
    public void onActionReceive(String action) {
        switch (action){
            case NotificationActions.ACTION_NEXT: next();break;
            case NotificationActions.ACTION_PREVIOUS: last();break;
            case NotificationActions.ACTION_PLAY: play();break;
            case NotificationActions.ACTION_PAUSE: pause();break;
            case NotificationActions.ACTION_LOVER: break;
            case NotificationActions.ACTION_PLAY_PAUSE:
                if(mediaPlayer.isPlaying()) pause();else play();
                break;
        }
    }

    @Override
    public void onActionReceive(long pos) {
        setProgress((int) pos);
    }
    //endregion

    //region Media operate method
    private void updateMediaMetadata(){
        if(stopped || mediaMetaChangedListener == null || mMusicPlayRequest == null || mMusicPlayRequest.getMusic() == null) return;
        MusicItem musicItem = mMusicPlayRequest.getMusic();
        mediaMetaChangedListener.onMediaMetaChanged(
                musicItem.getName(),
                musicItem.getSinger(),
                musicItem.getAlbum(),
                musicItem.getImage(),
                false,
                mediaPlayer.isPlaying(),
                mediaPlayer.getPosition(),
                mediaPlayer.getDuration()
        );
    }
    private void updateMediaMetadataPosition(){
        if(mediaMetaChangedListener == null || mMusicPlayRequest == null || mMusicPlayRequest.getMusic() == null) return;
        mediaMetaChangedListener.onMediaMetaChanged(
                mediaPlayer.isPlaying(),
                mediaPlayer.getPosition()
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
        if(mediaPlayer.getDuration() > 0) {
            mediaPlayer.play();
            return;
        }
        if(this.mMusicPlayRequest != null){
            play(this.mMusicPlayRequest.getMusic());
        }
        if(mAudioManager != null){
            volume = mAudioManager.getStreamVolume(AudioManager.STREAM_MUSIC)*100/maxAudioVolume;
        }
    }
    public void play(String uri, MusicItem musicItem){
        if(uri != null && !uri.isEmpty() && uri.equals(url)) {
            play();
            remote = true;
            return;
        }
        if(musicItem != null) {
            if(mMusicPlayRequest == null) {
                mMusicPlayRequest = new MusicPlayRequest();
            }
            mMusicPlayRequest.setMusic(musicItem);
        }
        play(uri);
        mediaPlayer.setSeekDiscontinuity(true);
        remote = true;
    }
    public void play(String uri){
        remote = false;
        if(uri == null || uri.isEmpty()) {
            pause();
            return;
        }
        mediaPlayer.setSeekDiscontinuity(false);
        mediaPlayer.play(uri);
        this.url = uri;
        if(mAudioManager != null){
            volume = mAudioManager.getStreamVolume(AudioManager.STREAM_MUSIC)*100/maxAudioVolume;
        }
    }
    public void play(int index){
        remote = false;
        if(this.mMusicPlayRequest == null) return;
        play(this.mMusicPlayRequest.getPlaylist().get(index));
    }
    public void play(MusicItem musicItem){
        remote = false;
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
        remote = false;
        this.mMusicPlayRequest = musicPlayRequest;
        if(this.mMusicPlayRequest.getMusic() != null){
            play(this.mMusicPlayRequest.getMusic());
        }
    }
    public void setMusicPlayRequest(MusicPlayRequest musicPlayRequest){
        this.mMusicPlayRequest = musicPlayRequest;
    }
    public void pause(){
        mediaPlayer.pause();
        if(changedListener != null){
            changedListener.onPaused();
        }
    }
    public void pauseByRemote(){
        mediaPlayer.pause();
    }
    public void setProgress(int percent){
        double percentDouble = percent*1.0/1000;
        percent = (int) (mediaPlayer.getDuration() * percentDouble);
        if(percent > 0 && percent <= mediaPlayer.getDuration()){
            mediaPlayer.seekTo(percent);
        }
    }
    public void setPosition(int pos){
        if(pos <= 0) return;
        int posReal = pos + 20 + Math.min(50, Math.abs(playingChangedDelay*2));
        if(posReal - mediaPlayer.getPosition() > 150) {
            mediaPlayer.seekTo(posReal);
        }else if(pos + 100 < mediaPlayer.getPosition()){
            mediaPlayer.seekTo(pos+20);
        }
    }
    public void setVolume(int percent){
        if(mAudioManager == null || volume == percent) return;
        volume = percent;
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
        if(changedListener != null){
            changedListener.onVolumeChanged(volume);
        }
    }

    //endregion
    @SuppressLint("DefaultLocale")
    private String parseMMSS(long milliseconds){
        if(milliseconds <= 0) return "00:00";
        long second = milliseconds / 1000;
        long minute = second / 60;
        second = second - minute * 60;
        return String.format("%02d", minute) + ":" + String.format("%02d", second);
    }
}
