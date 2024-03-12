package com.picapico.audioshare.musiche.notification;

import android.annotation.SuppressLint;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Intent;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.support.v4.media.MediaMetadataCompat;
import android.support.v4.media.session.MediaSessionCompat;
import android.support.v4.media.session.PlaybackStateCompat;

import androidx.annotation.Nullable;
import androidx.core.app.NotificationCompat;

import com.picapico.audioshare.MainActivity;
import com.picapico.audioshare.R;

import java.io.IOException;
import java.io.InputStream;
import java.net.URL;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

public class NotificationService extends Service {
    public static int NOTIFICATION_ID = 1;
    public  static final String CHANNEL_ID = "com.picapico.audioshare.channel.audio";
    public  static final String MEDIA_SESSION_TAG = "com.picapico.audioshare.media.session";
    private static final long MEDIA_SESSION_ACTIONS =
            PlaybackStateCompat.ACTION_PLAY
                    | PlaybackStateCompat.ACTION_PAUSE
                    | PlaybackStateCompat.ACTION_PLAY_PAUSE
                    | PlaybackStateCompat.ACTION_SKIP_TO_NEXT
                    | PlaybackStateCompat.ACTION_SKIP_TO_PREVIOUS
                    | PlaybackStateCompat.ACTION_SEEK_TO;

    private final ExecutorService mExecutorService = Executors.newSingleThreadExecutor();
    private final Handler mHandler = new Handler(Looper.getMainLooper());
    private PendingIntent mPendingPlay;
    private PendingIntent mPendingPrevious;
    private PendingIntent mPendingNext;
    private PendingIntent mPendingLover;
    private androidx.core.app.NotificationCompat.Builder mNotificationBuilder;
    private MediaSessionCompat mMediaSession ;
    private Notification mNotification ;

    @Override
    public void onCreate() {
        super.onCreate();
        mMediaSession = new MediaSessionCompat(this, MEDIA_SESSION_TAG);
        mMediaSession.setActive(true);
    }

    public void setMediaSessionCallback(NotificationCallback callback){
        mMediaSession.setCallback(callback, mHandler);
        NotificationReceiver.setOnActionReceiveListener(callback);
    }

    private void createNotificationChannel() {
        String description = "通知栏播放控制";
        if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
            NotificationChannel serviceChannel = new NotificationChannel(
                    CHANNEL_ID,
                    "Musiche Notification",
                    NotificationManager.IMPORTANCE_LOW
            );
            serviceChannel.setDescription(description);
            serviceChannel.enableLights(false);
            serviceChannel.enableVibration(false);
            NotificationManager manager = getSystemService(NotificationManager.class);
            if(manager != null) manager.createNotificationChannel(serviceChannel);
        }
    }

    private void initNotification(){
        createNotificationChannel();

        int flag = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            flag = PendingIntent.FLAG_IMMUTABLE;
        }

        //绑定事件通过创建的具体广播去接收即可。
        Intent infoIntent = new Intent(this, MainActivity.class);
        infoIntent.setAction(NotificationActions.ACTION_SHOW);
        @SuppressLint("WrongConstant") PendingIntent pendingInfo =
                PendingIntent.getActivity(this, 0, infoIntent, flag);
        Intent preIntent = new Intent(this, NotificationReceiver.class);
        preIntent.setAction(NotificationActions.ACTION_PREVIOUS);
        mPendingPrevious = PendingIntent.getBroadcast(this, 1, preIntent, flag);
        Intent playIntent = new Intent(this, NotificationReceiver.class);
        playIntent.setAction(NotificationActions.ACTION_PLAY_PAUSE);
        mPendingPlay = PendingIntent.getBroadcast(this, 2, playIntent, flag);
        Intent nextIntent = new Intent(this, NotificationReceiver.class);
        nextIntent.setAction(NotificationActions.ACTION_NEXT);
        mPendingNext = PendingIntent.getBroadcast(this, 3, nextIntent, PendingIntent.FLAG_IMMUTABLE);
        Intent loverIntent = new Intent(this, NotificationReceiver.class);
        loverIntent.setAction(NotificationActions.ACTION_LOVER);
        mPendingLover = PendingIntent.getBroadcast(this, 4, loverIntent, PendingIntent.FLAG_IMMUTABLE);
        mNotificationBuilder  = new androidx.core.app.NotificationCompat.Builder(this, CHANNEL_ID)
                .setStyle(new androidx.media.app.NotificationCompat.MediaStyle()
                        .setShowActionsInCompactView(0, 1, 2, 3)
                        .setShowCancelButton(false)
                        .setMediaSession(mMediaSession.getSessionToken()))
                .setContentIntent(pendingInfo)
                .setSmallIcon(R.mipmap.ic_launcher)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setPriority(NotificationCompat.PRIORITY_MAX);
    }

    private String mLastArtWork = "";
    private Bitmap mLargeIcon;
    private Bitmap mDefaultLargeIcon;
    private void updateNotification(String title, String artist, String album, boolean playing, boolean lover, int position, int duration, Bitmap largeIcon){

        if (mNotification == null) {
            initNotification();
        }

        int iconPlayPause = R.drawable.play;
        String titlePlayPause = "pause";
        if(playing){
            iconPlayPause = R.drawable.pause;
            titlePlayPause = "play";
        }
        int iconLover = lover ? R.drawable.lover_on : R.drawable.lover_off;

        mNotificationBuilder.clearActions();
        mNotification = mNotificationBuilder
                .setContentTitle(title)
                .setContentText(artist)
                .setSubText(album)
                .setLargeIcon(largeIcon)
                .addAction(R.drawable.last, "prev", mPendingPrevious)
                .addAction(iconPlayPause, titlePlayPause, mPendingPlay)
                .addAction(R.drawable.next, "next", mPendingNext)
                .addAction(iconLover, "lover", mPendingLover).build();
        startForeground(NOTIFICATION_ID, mNotification);
        if(!playing) {
            stopForeground(false);
        }
        if(duration > 0) {
            updateMetaData(title, artist, album, playing, position, duration, largeIcon);
        }
    }

    private void updateMetaData(String title, String artist, String album, boolean playing, int position, int duration, Bitmap largeIcon){
        MediaMetadataCompat.Builder mMetaData = new MediaMetadataCompat.Builder()
                .putString(MediaMetadataCompat.METADATA_KEY_TITLE, title)
                .putString(MediaMetadataCompat.METADATA_KEY_ARTIST, artist)
                .putString(MediaMetadataCompat.METADATA_KEY_ALBUM, album)
                .putString(MediaMetadataCompat.METADATA_KEY_ALBUM_ARTIST, artist)
                .putLong(MediaMetadataCompat.METADATA_KEY_DURATION, duration)
                .putBitmap(MediaMetadataCompat.METADATA_KEY_ALBUM_ART, largeIcon);
        mMediaSession.setMetadata(mMetaData.build());
        updateMediaPosition(playing, position);
    }

    private void updateMediaPosition(boolean playing, int position){
        int state = playing ? PlaybackStateCompat.STATE_PLAYING :
                PlaybackStateCompat.STATE_PAUSED;
        mMediaSession.setPlaybackState(new PlaybackStateCompat.Builder()
                .setActions(MEDIA_SESSION_ACTIONS)
                .setState(state, position, 1)
                .build());
    }

    public void setMetaData(boolean playing, int position){
        updateMediaPosition(playing, position);
    }
    public void setMetaData(String title, String artist, String album, String artwork, boolean lover, boolean playing, int position, int duration) {
        if(title == null || title.isEmpty()){
            updateMediaPosition(playing, position);
        }else {
            if (artwork.equals(mLastArtWork) && mLargeIcon != null) {
                updateNotification(title, artist, album, playing, lover, position, duration, mLargeIcon);
            }else {
                mExecutorService.execute(() -> {
                    Bitmap largeIcon;
                    try {
                        URL url = new URL(artwork);
                        InputStream inputStream = url.openStream();
                        largeIcon = BitmapFactory.decodeStream(inputStream);
                    } catch (IOException e) {
                        if(mDefaultLargeIcon == null) {
                            mDefaultLargeIcon = BitmapFactory.decodeResource(getResources(), R.mipmap.ic_launcher);
                        }
                        largeIcon = mDefaultLargeIcon;
                    }
                    Bitmap finalLargeIcon = largeIcon;
                    mHandler.post(() -> {
                        mLastArtWork = artwork;
                        if(mLargeIcon != null && !mLargeIcon.equals(mDefaultLargeIcon)) {
                            mLargeIcon.recycle();
                        }
                        mLargeIcon = finalLargeIcon;
                        updateNotification(title, artist, album, playing, lover, position, duration, mLargeIcon);
                    });
                });
            }
        }
    }

    @Override
    public void onTaskRemoved(Intent rootIntent) {
        stopForeground(true);
    }

    @Override
    public void onDestroy() {
        super.onDestroy();
    }

    @Nullable
    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }
}
