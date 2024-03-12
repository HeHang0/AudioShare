package com.picapico.audioshare.musiche.notification;

public interface OnActionReceiveListener {
    void onActionReceive(String action);
    void onActionReceive(long pos);
}