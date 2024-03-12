package com.picapico.audioshare.musiche.notification;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class NotificationReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        String action = intent.getAction();
        if(action == null) return;
        if(mActionReceiveListener != null) {
            mActionReceiveListener.onActionReceive(action);
        }
    }

    private static OnActionReceiveListener mActionReceiveListener;

    public static void setOnActionReceiveListener(OnActionReceiveListener listener) {
        mActionReceiveListener = listener;
    }
}