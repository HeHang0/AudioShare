package com.picapico.audioshare.musiche;

import androidx.annotation.IntDef;

import org.json.JSONException;
import org.json.JSONObject;

import java.util.HashMap;
import java.util.Map;

public class RemoteMessage {
    public static final int MessageTypeNone = 0;
    public static final int MessageTypePlay = 1;
    public static final int MessageTypePause = 2;
    public static final int MessageTypeGetPosition = 3;
    public static final int MessageTypeSetPosition = 4;
    public static final int MessageTypeVolume = 5;
    public static final int MessageTypeInfo = 6;
    public static final int MessageTypeChannel = 7;

    public int getType() {
        return type;
    }

    public RemoteMessage setType(int type) {
        this.type = type;
        return this;
    }

    public MusicItem getMusic() {
        return music;
    }

    public RemoteMessage setMusic(MusicItem music) {
        this.music = music;
        return this;
    }

    public String getUrl() {
        return url;
    }

    public RemoteMessage setUrl(String url) {
        this.url = url;
        return this;
    }

    public int getPosition() {
        return position;
    }

    public RemoteMessage setPosition(int position) {
        this.position = position;
        return this;
    }

    public int getVolume() {
        return volume;
    }

    public RemoteMessage setVolume(int volume) {
        this.volume = volume;
        return this;
    }

    public String getName() {
        return name;
    }

    public RemoteMessage setName(String name) {
        this.name = name;
        return this;
    }

    public int getChannel() {
        return channel;
    }

    public RemoteMessage setChannel(@AudioPlayer.ChannelType int channel) {
        this.channel = channel;
        return this;
    }

    public int getPort() {
        return port;
    }

    public RemoteMessage setPort(int port) {
        this.port = port;
        return this;
    }

    @IntDef(
            value = {
                    MessageTypeNone,
                    MessageTypePlay,
                    MessageTypePause,
                    MessageTypeGetPosition,
                    MessageTypeSetPosition,
                    MessageTypeVolume,
                    MessageTypeInfo,
                    MessageTypeChannel
            }
    )
    public @interface MessageType {}
    private @MessageType int type = MessageTypeNone;
    private MusicItem music;
    private String url;
    private String name;
    private int position = -1;
    private int volume = -1;
    private int port = -1;
    private @AudioPlayer.ChannelType int channel = AudioPlayer.ChannelTypeNone;
    public static RemoteMessage of(@MessageType int type){
        RemoteMessage msg = new RemoteMessage();
        msg.setType(type);
        return msg;
    }

    public static RemoteMessage of(String message){
        try {
            JSONObject jsonObject = new JSONObject(message);
            RemoteMessage msg = new RemoteMessage();
            if(jsonObject.has("type")){
                msg.setType(jsonObject.getInt("type"));
            }
            if(jsonObject.has("position")){
                msg.setPosition(jsonObject.getInt("position"));
            }
            if(jsonObject.has("volume")){
                msg.setVolume(jsonObject.getInt("volume"));
            }
            if(jsonObject.has("channel")){
                msg.setChannel(jsonObject.getInt("channel"));
            }
            if(jsonObject.has("port")){
                msg.setPort(jsonObject.getInt("port"));
            }
            if(jsonObject.has("name")){
                msg.setName(jsonObject.getString("name"));
            }
            if(jsonObject.has("url")){
                msg.setUrl(jsonObject.getString("url"));
            }
            if(jsonObject.has("music")){
                msg.setMusic(MusicItem.of(jsonObject.getJSONObject("music")));
            }
            return msg;
        } catch (JSONException e) {
            return null;
        }
    }

    public Map<String, Object> toMap(){
        Map<String, Object> data = new HashMap<>();
        data.put("type", type);
        if(music != null) data.put("music", music.toMap());
        if(url != null) data.put("url", url);
        if(name != null) data.put("name", name);
        if(position >= 0) data.put("position", position);
        if(volume >= 0) data.put("volume", volume);
        if(channel >= 0) data.put("channel", channel);
        if(port >= 0) data.put("port", port);
        return data;
    }
    public String toJson(){
        return new JSONObject(toMap()).toString();
    }
}
