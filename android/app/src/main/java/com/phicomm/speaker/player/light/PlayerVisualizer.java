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
    public PlayerVisualizer(int sessionId){
        audioSessionId = sessionId;
        if(!LedLight.getEnabled()) {
            LedLight.setOnLoadSuccessListener(this::initVisualizer);
            return;
        }
        initVisualizer();
    }

    private void initVisualizer(){
        try {
            this.mVisualizer = new Visualizer(audioSessionId);
            this.mVisualizer.setCaptureSize(Visualizer.getCaptureSizeRange()[1]);
            this.mVisualizer.setScalingMode(0);
            this.mVisualizer.setDataCaptureListener(this, Visualizer.getMaxCaptureRate(), false, true);
            this.mVisualizer.setEnabled(true);
        }catch (Exception e){
            Log.e(TAG, "initialize visualizer error");
            e.printStackTrace();
        }
    }

    public void stop(){
        if (this.mVisualizer != null) {
            this.mVisualizer.release();
        }
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
