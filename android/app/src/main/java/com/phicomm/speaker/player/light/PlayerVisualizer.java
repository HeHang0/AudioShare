package com.phicomm.speaker.player.light;

import android.graphics.Color;
import android.media.audiofx.Visualizer;
import android.util.Log;

public class PlayerVisualizer implements Visualizer.OnDataCaptureListener {
    private static final String TAG = "AudioShareVisualizer";

    private float amp2 = 0.0F;

    private float amp3 = 0.0F;

    private Visualizer mVisualizer;
    private final int audioSessionId;
    private PlayerVisualizer(){
        this(0);
    }
    private PlayerVisualizer(int sessionId){
        audioSessionId = sessionId;
        if(!LedLight.getEnabled()) {
            LedLight.setOnLoadSuccessListener(this::initVisualizer);
            return;
        }
        initVisualizer();
    }
    private static int startCount = 0;
    private static PlayerVisualizer instance = null;
    private static final Object mStateLock = new Object();
    public static void start(){
        if(!LedLight.supported()) {
            return;
        }
        Log.i(TAG, "visualizer supported");
        synchronized (mStateLock) {
            startCount++;
            PlayerVisualizer.updateTimeMillis();
            if(instance != null) return;
            instance = new PlayerVisualizer();
        }
    }

    public static void stop(){
        Log.i(TAG, "ready to stop visualizer");
        if(!LedLight.supported()) return;
        synchronized (mStateLock) {
            if(--startCount > 0){
                return;
            }
            if (instance != null) {
                instance.release();
            }
            instance = null;
        }
    }

    private void initVisualizer(){
        try {
            this.mVisualizer = new Visualizer(this.audioSessionId);
            this.mVisualizer.setCaptureSize(Visualizer.getCaptureSizeRange()[1]);
            this.mVisualizer.setScalingMode(0);
            this.mVisualizer.setDataCaptureListener(this, Visualizer.getMaxCaptureRate(), false, true);
            this.mVisualizer.setEnabled(true);
            Log.e(TAG, "initialize visualizer");
        }catch (Exception e){
            Log.e(TAG, "initialize visualizer error");
            e.printStackTrace();
        }
    }

    private void release(){
        if (this.mVisualizer != null) {
            this.mVisualizer.release();
        }
        this.mVisualizer = null;
        LedLight.setColor(32767L, 0);
    }

    private float move5Avg(float paramFloat) {
        float amp1 = this.amp2;
        this.amp2 = this.amp3;
        this.amp3 = paramFloat;
        return (amp1 + this.amp2 + this.amp3) / 3.0F;
    }
    @Override
    public void onFftDataCapture(Visualizer visualizer, byte[] waveform, int samplingRate) {
        float f1 = 0.0F;
        samplingRate = 2;
        while (samplingRate < waveform.length) {
            int i = (int)Math.hypot(waveform[samplingRate], waveform[samplingRate + 1]);
            float f = f1;
            if (f1 < i)
                f = i;
            samplingRate += 2;
            f1 = f;
        }
        f1 = move5Avg(f1) / 180.0F;
        if (f1 == 0) {
            LedLight.setColor(32767L, 0);
            return;
        }
        updateColor();
        f1 = (f1 * 0.8F) + 0.2F;
        float[] hsv = new float[]{mHue, 1.0F, f1};
        LedLight.setColor(32767L, 0xFFFFFF & Color.HSVToColor(hsv));
    }

    private float mHue = 0.0F;

    private static long mTimeMillis = System.currentTimeMillis();

    private synchronized void updateColor(){
        mHue = ((float) (System.currentTimeMillis() - mTimeMillis) / 51) % 360;
    }

    public static synchronized void updateTimeMillis(){
        mTimeMillis = System.currentTimeMillis();
    }

    @Override
    public void onWaveFormDataCapture(Visualizer visualizer, byte[] fft, int samplingRate) {

    }
}
