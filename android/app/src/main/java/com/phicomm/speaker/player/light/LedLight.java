package com.phicomm.speaker.player.light;

import android.os.Build;
import android.util.Log;

public class LedLight {
    private static final String TAG = "AudioShareLedLight";
    private static int mColor = 0;
    private static boolean enabled = false;
    public interface OnLoadSuccessListener {
        void OnLoadSuccess();
    }
    private static OnLoadSuccessListener mOnLoadSuccessListener;

    public static void setOnLoadSuccessListener(OnLoadSuccessListener listener){
        mOnLoadSuccessListener = listener;
    }

    public static synchronized boolean getEnabled(){
        return enabled;
    }

    public static synchronized void setEnable(boolean enabled){
        LedLight.enabled = enabled;
    }

    public static void setColor(long paramLong, int paramInt) {
        if (enabled && mColor != paramInt) {
            try{
                set_color(paramLong, paramInt);
            }catch (UnsatisfiedLinkError | Exception e){
                Log.e(TAG, "load set_color error: " + e);
            }
            mColor = paramInt;
        }
    }

    public static native void set_color(long paramLong, int paramInt);

    static {
        if(Build.MANUFACTURER.equalsIgnoreCase("phicomm")) {
            Log.i(TAG, System.currentTimeMillis() + "load library start");
            new Thread(() -> {
                try{
                    System.loadLibrary("ledLight-jni");
                    CommandExecution.CommandResult result = CommandExecution.execCommand("setenforce 0", true);
                    if (result.result == 0) {
                        setEnable(true);
                        if(mOnLoadSuccessListener != null) {
                            mOnLoadSuccessListener.OnLoadSuccess();
                        }
                        Log.i(TAG, "load library success " + result.successMsg);
                    }else {
                        Log.e(TAG, "load library error: " + result.errorMsg);
                    }
                } catch (Error | Exception e) {
                    Log.e(TAG, "load library error: " + e);
                }
            }).start();
        }
    }
}
