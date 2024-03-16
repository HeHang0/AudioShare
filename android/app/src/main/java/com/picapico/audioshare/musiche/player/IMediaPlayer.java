package com.picapico.audioshare.musiche.player;

import androidx.annotation.IntDef;

public interface IMediaPlayer {
    interface Listener {
        @IntDef({STATE_IDLE, STATE_BUFFERING, STATE_READY, STATE_ENDED})
        @interface State {}
        int STATE_IDLE = 1;
        int STATE_BUFFERING = 2;
        int STATE_READY = 3;
        int STATE_ENDED = 4;
        void onPlayingChanged(boolean playing);
        void onPlaybackStateChanged(@State int playbackState);
        void onDurationChanged(int position, int duration);
        void onDeviceVolumeChanged(int volume);
        void onPositionChanged(boolean playing, int position, int duration);
    }
    public interface RealtimePositionCallback{
        void onPosition(int position);
    }
    void play();
    void play(String url);
    void pause();
    void seekTo(int position);
    void setVolume(int volume);
    int getDuration();
    int getPosition();
    boolean isPlaying();
    void getRealtimePosition(RealtimePositionCallback callback);
    void setMediaChangedListener(Listener listener);
}
