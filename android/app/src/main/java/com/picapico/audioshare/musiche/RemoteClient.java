package com.picapico.audioshare.musiche;

import com.koushikdutta.async.http.WebSocket;
import com.picapico.audioshare.musiche.player.AudioPlayer;

import org.json.JSONException;
import org.json.JSONObject;

import java.util.HashMap;
import java.util.Map;

public class RemoteClient {
    private WebSocket webSocket;
    private String address;
    private int port;
    private String name;
    private @AudioPlayer.ChannelType int channel = AudioPlayer.ChannelTypeStereo;

    public void setWebSocket(WebSocket webSocket) {
        this.webSocket = webSocket;
    }

    public String getAddress() {
        return address;
    }

    public void setAddress(String address) {
        this.address = address;
    }

    public void setPort(int port) {
        this.port = port;
    }

    public String getName() {
        return name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public int getChannel() {
        return channel;
    }

    public RemoteClient setChannel(int channel) {
        this.channel = channel;
        return this;
    }

    public void send(RemoteMessage message){
        if(webSocket != null && channel != AudioPlayer.ChannelTypeNone) {
            webSocket.send(message.toJson());
        }
    }

    private RemoteClient(){

    }

    public static RemoteClient of(WebSocket webSocket, String address){
        RemoteClient client = new RemoteClient();
        client.setWebSocket(webSocket);
        client.setAddress(address);
        return client;
    }

    public static RemoteClient of(String name, String address, int port, int channel){
        RemoteClient client = new RemoteClient();
        client.setName(name);
        client.setAddress(address);
        client.setPort(port);
        client.setChannel(channel);
        return client;
    }

    public static RemoteClient of(JSONObject jsonObject){
        RemoteClient client = new RemoteClient();
        try {
            if(jsonObject.has("address")){
                client.setAddress(jsonObject.getString("address"));
            }
            if(jsonObject.has("port")){
                client.setPort(jsonObject.getInt("port"));
            }
            if(jsonObject.has("name")){
                client.setName(jsonObject.getString("name"));
            }
            if(jsonObject.has("channel")){
                client.setChannel(jsonObject.getInt("channel"));
            }
        } catch (JSONException ignore) {
        }
        return client;
    }

    public Map<String, Object> toMap(){
        Map<String, Object> data = new HashMap<>();
        if(address != null) data.put("address", address);
        if(port > 0) data.put("port", port);
        if(name != null) data.put("name", name);
        if(channel >= 0) data.put("channel", channel);
        return data;
    }
}
