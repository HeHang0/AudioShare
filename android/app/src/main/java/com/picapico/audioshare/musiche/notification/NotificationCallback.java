package com.picapico.audioshare.musiche.notification;

import android.support.v4.media.session.MediaSessionCompat;

public class NotificationCallback extends MediaSessionCompat.Callback implements OnActionReceiveListener {

    private OnActionReceiveListener mActionReceiveListener;

    public void setOnActionReceiveListener(OnActionReceiveListener listener) {
        mActionReceiveListener = listener;
    }

    @Override
    public void onActionReceive(String action) {
        if(mActionReceiveListener != null) mActionReceiveListener.onActionReceive(action);
    }
    @Override
    public void onActionReceive(long pos) {
        if(mActionReceiveListener != null) mActionReceiveListener.onActionReceive(pos);
    }
    @Override
    public void onPlay() {
        onActionReceive(NotificationActions.ACTION_PLAY);
    }

    @Override
    public void onPause() {
        onActionReceive(NotificationActions.ACTION_PAUSE);
    }

    @Override
    public void onSkipToNext() {
        onActionReceive(NotificationActions.ACTION_NEXT);
    }

    @Override
    public void onSkipToPrevious() {
        onActionReceive(NotificationActions.ACTION_PREVIOUS);
    }

    @Override
    public void onStop() {
        onActionReceive(NotificationActions.ACTION_PAUSE);
    }

    @Override
    public void onSeekTo(long pos) {
        onActionReceive(pos);
    }
}
