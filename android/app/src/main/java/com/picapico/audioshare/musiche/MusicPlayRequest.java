package com.picapico.audioshare.musiche;

import android.util.Log;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;
import java.util.Objects;

public class MusicPlayRequest {
    private static final String TAG = "AudioShareMusicPlayReq";
    private MusicItem music;
    private int index = 0;
    private List<MusicItem> playlist = new ArrayList<>();

    public static MusicPlayRequest of(JSONObject jsonObject){
        MusicPlayRequest musicPlayRequest = new MusicPlayRequest();
        try {
            MusicItem musicItem = null;
            if(jsonObject.has("music")){
                musicItem = MusicItem.of(jsonObject.getJSONObject("music"));
            }
            if(jsonObject.has("playlist")){
                JSONArray jsonArray = jsonObject.getJSONArray("playlist");
                List<MusicItem> playlist = musicPlayRequest.getPlaylist();
                for (int i = 0; i < jsonArray.length(); i++) {
                    MusicItem music = MusicItem.of(jsonArray.getJSONObject(i));
                    if(musicItem != null &&
                            Objects.equals(musicItem.getId(), music.getId()) &&
                            Objects.equals(musicItem.getType(), music.getType())) {
                        musicPlayRequest.setIndex(i);
                        playlist.add(music);
                        musicPlayRequest.setMusic(music);
                    }else {
                        playlist.add(music);
                    }
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "parse music play request error", e);
        }
        if(musicPlayRequest.getMusic() == null && musicPlayRequest.getPlaylist().size() > 0){
            musicPlayRequest.setMusic(musicPlayRequest.getPlaylist().get(0));
        }
        return musicPlayRequest;
    }

    public MusicItem getMusic() {
        return music;
    }

    public void setMusic(MusicItem music) {
        this.music = music;
    }

    public List<MusicItem> getPlaylist() {
        return playlist;
    }

    public void setPlaylist(List<MusicItem> list) {
        this.playlist = list;
    }

    public int getIndex() {
        return index;
    }

    public void setIndex(int index) {
        this.index = index;
    }
}
