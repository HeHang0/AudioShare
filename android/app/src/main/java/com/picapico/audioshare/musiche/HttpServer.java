package com.picapico.audioshare.musiche;

import android.annotation.SuppressLint;
import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.AssetManager;
import android.media.AudioManager;
import android.os.Build;
import android.util.Log;

import com.koushikdutta.async.AsyncNetworkSocket;
import com.koushikdutta.async.ByteBufferList;
import com.koushikdutta.async.http.AsyncHttpClient;
import com.koushikdutta.async.http.AsyncHttpResponse;
import com.koushikdutta.async.http.Headers;
import com.koushikdutta.async.http.WebSocket;
import com.koushikdutta.async.http.server.AsyncHttpServer;
import com.koushikdutta.async.http.server.AsyncHttpServerRequest;
import com.koushikdutta.async.http.server.AsyncHttpServerResponse;
import com.koushikdutta.async.http.server.HttpServerRequestCallback;
import com.picapico.audioshare.musiche.player.AudioPlayer;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.InputStream;
import java.lang.reflect.Method;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Objects;
import java.util.Timer;
import java.util.TimerTask;

public class HttpServer implements AudioPlayer.OnChangedListener {
    private static final String TAG = "AudioShareHttpServer";
    private String mVersionName = "";
    private String mDeviceName = "";
    int mServerPort;
    AsyncHttpServer mServer;
    AudioPlayer mAudioPlayer;
    List<WebSocket> mWebClients = new ArrayList<>();
    Map<String, RemoteClient> mRemoteClients = new HashMap<>();
    BroadcastReceiver mBroadcastReceiver;
    SharedPreferences mPreferences;
    AssetManager mAssetManager;
    Context mContext;
    public HttpServer(Context context, int port) {
        mServerPort = port;
        mContext = context;
        mAudioPlayer = new AudioPlayer(context);
        initDeviceName();
        initBroadcastReceiver();
    }

    //region Initialization
    public void setAudioManager(AudioManager audioManager){
        mAudioPlayer.setAudioManager(audioManager);
        mAudioPlayer.setOnLoadSuccessListener(this);
    }

    private void setChannel(@AudioPlayer.ChannelType int channel){
        mAudioPlayer.setChannel(channel);
        mPreferences.edit().putInt("channel", channel).apply();
    }

    private void initBroadcastReceiver(){
        mBroadcastReceiver = new BroadcastReceiver();
        mBroadcastReceiver.setRemoteServerReceivedListener(this::onRemoteServerReceived);
        mBroadcastReceiver.start();
    }

    private void initDeviceName(){
        String marketingName = getMarketingName();
        if(marketingName == null || marketingName.isEmpty()){
            String manufacturer = Build.MANUFACTURER;
            String model = Build.MODEL;
            if(manufacturer == null || manufacturer.isEmpty() ||
                    "unknown".equalsIgnoreCase(manufacturer)){
                marketingName = model;
            }else {
                marketingName = manufacturer + " " + model;
            }
        }
        mDeviceName = marketingName;
    }
    //endregion

    //region Public Methods
    public HttpServer start(){
        stop();
        try {
            mServer = new AsyncHttpServer(){
                @Override
                public boolean onRequest(AsyncHttpServerRequest request, AsyncHttpServerResponse response) {
                    return onClientRequest(request, response);
                }
            };
            mServer.listen(mServerPort);
            initHandler();
            Log.i(TAG, "start http server on: " + mServer);
        }catch (Exception e){
            Log.e(TAG, "start http server error: ", e);
        }
        return this;
    }
    public void stop(){
        if(mServer == null) return;
        try {
            mServer.stop();
        }catch (Exception e){
            Log.e(TAG, "stop server error");
        }
        mServer = null;
    }
    public void pause(){
        if(mAudioPlayer != null) mAudioPlayer.pause();
    }
    public static String getMarketingName() {
        try {
            @SuppressLint("PrivateApi") Class<?> systemPropertiesClass = Class.forName("android.os.SystemProperties");
            Method getMethod = systemPropertiesClass.getMethod("get", String.class);
            return (String) getMethod.invoke(null, "ro.config.marketing_name");
        } catch (Exception ignore) { }
        return null;
    }

    public void setVersionName(String mVersionName){
        this.mVersionName = mVersionName;
    }

    public void setSharedPreferences(SharedPreferences preferences){
        this.mPreferences = preferences;
        mAudioPlayer.setChannel(mPreferences.getInt("channel", AudioPlayer.ChannelTypeStereo));
    }

    public void setAssetManager(AssetManager assetManager){
        this.mAssetManager = assetManager;
    }
    //endregion

    //region RemoteServerMessage
    private boolean isPositionSynchronized = false;
    private boolean isPositionSynchronizing = false;
    private void onRemoteServerMessage(String message, String address){
//        Log.d(TAG, "receive remote server message: " + message);
        RemoteMessage msg = RemoteMessage.of(message);
        if(msg == null) return;
        RemoteClient client = mRemoteClients.get(address);
        switch (msg.getType()){
            case RemoteMessage.MessageTypeGetPosition:
                mAudioPlayer.getPosition();
                break;
            case RemoteMessage.MessageTypePause:
                mAudioPlayer.pause();
                break;
            case RemoteMessage.MessageTypeInfo:
                if(client != null){
                    client.setName(msg.getName());
                    client.setPort(msg.getPort());
                }
                break;
            case RemoteMessage.MessageTypeChannel:
                setChannel(msg.getChannel());
                break;
            case RemoteMessage.MessageTypeVolume:
                mAudioPlayer.setVolume(msg.getVolume());
                break;
            case RemoteMessage.MessageTypePlay:
                isPositionSynchronized = false;
                mAudioPlayer.setVolume(msg.getVolume());
                if(client != null && client.getChannel() == AudioPlayer.ChannelTypeNone){
                    client.setChannel(AudioPlayer.ChannelTypeStereo);
                }
                mAudioPlayer.play(msg.getUrl(), msg.getMusic());
                break;
            case RemoteMessage.MessageTypeSetPosition:
                isPositionSynchronized = true;
                int diff = (int) (System.currentTimeMillis() - getPositionTime) / 2;
                mAudioPlayer.setPosition(msg.getPosition() + diff);
                isPositionSynchronizing = false;
                break;
        }
    }

    private void onRemoteServerReceived(String hostname, String port){
        if(mRemoteClients.containsKey(hostname)) return;
        String url = String.format("http://%s:%s/sws", hostname, port);
        AsyncHttpClient.getDefaultInstance().websocket(url, (String) null, (e, webSocket) -> {
            if (e != null) {
                Log.e(TAG, "connect ws server error", e);
                return;
            }
            this.onRemoteServerSocket(webSocket);
        });
    }

    private void onRemoteServerSocket(WebSocket webSocket){
        try{
            AsyncNetworkSocket socket = (AsyncNetworkSocket) webSocket.getSocket();
            String address = socket.getRemoteAddress().getHostString();
            if(mRemoteClients.containsKey(address)) {
                webSocket.close();
                return;
            }
            mRemoteClients.put(address, RemoteClient.of(webSocket, address).setChannel(
                    mPreferences.getInt("channel-"+address, AudioPlayer.ChannelTypeStereo)));
            mBroadcastReceiver.stop();
            webSocket.setClosedCallback(e -> mRemoteClients.remove(address));
            webSocket.setStringCallback(s -> onRemoteServerMessage(s, address));
            webSocket.send(RemoteMessage
                    .of(RemoteMessage.MessageTypeInfo)
                    .setName(mDeviceName)
                    .setPort(mServerPort)
                    .toJson());
            Log.d(TAG, "remote server connected: " + address);
        }catch (Exception ignore){}
    }
    //endregion

    //region AudioPlayer listener
    @Override
    public void onPositionChanged() {
        sendWSMessage(mAudioPlayer.getStatus().toString());
    }

    @Override
    public void onPositionChanged(long position) {
        sendServerWSMessage(RemoteMessage.of(RemoteMessage.MessageTypeSetPosition)
                .setPosition((int) position));
    }

    @Override
    public void onPaused() {
        sendServerWSMessage(RemoteMessage.of(RemoteMessage.MessageTypePause));
    }

    @Override
    public void onVolumeChanged(int volume) {
        sendServerWSMessage(RemoteMessage.of(RemoteMessage.MessageTypeVolume)
                .setVolume(volume));
    }

    private long getPositionTime = 0;
    @Override
    public void onPlaying(boolean remote, String url, MusicItem music, int volume) {
        if(isPositionSynchronized && url != null && remote) return;
        RemoteMessage message;
        if(remote){
            if(isPositionSynchronizing) return;
            isPositionSynchronizing = true;
            getPositionTime = System.currentTimeMillis();
            message = RemoteMessage.of(RemoteMessage.MessageTypeGetPosition);
        }else {
            message = RemoteMessage.of(RemoteMessage.MessageTypePlay)
                    .setUrl(url)
                    .setMusic(music)
                    .setVolume(volume);
        }
        sendServerWSMessage(message);
    }

    public AudioPlayer getAudioPlayer() {
        return mAudioPlayer;
    }
    //endregion

    //region Handler
    private void initHandler(){
        mServer.websocket("/ws", websocketHandler);
        mServer.websocket("/sws", serverWebsocketHandler);
        mServer.get("/version", getVersion);
        mServer.post("/version", getVersion);
        mServer.get("/config", config);
        mServer.post("/config", config);
        mServer.get("/storage", getStorage);
        mServer.post("/storage", setStorage);
        mServer.post("/title", empty);
        mServer.post("/media", empty);
        mServer.post("/fadein", empty);
        mServer.post("/delayExit", delayExit);
        mServer.post("/gpu", empty);
        mServer.post("/play", play);
        mServer.post("/updatelist", updateList);
        mServer.post("/pause", pause);
        mServer.post("/progress", progress);
        mServer.post("/volume", volume);
        mServer.post("/status", status);
        mServer.post("/window", empty);
        mServer.post("/maximize", empty);
        mServer.post("/minimize", empty);
        mServer.post("/loop", loop);
        mServer.post("/quality", quality);
        mServer.post("/exit", pause);
        mServer.post("/hide", empty);
        mServer.post("/minimize", empty);
        mServer.post("/fonts", empty);
        mServer.post("/image", image);
        mServer.post("/theme", empty);
        mServer.post("/lyric", empty);
        mServer.post("/lyricline", empty);
        mServer.post("/hotkey", empty);
        mServer.post("/proxy", proxyPost);
        mServer.get("/proxy", proxyGet);
        mServer.get("/remote/clients", getRemoteClients);
        mServer.post("/remote/client", updateRemoteClient);
        mServer.get(".*", getStatic);
    }
    private boolean onClientRequest(AsyncHttpServerRequest request, AsyncHttpServerResponse response) {
        setCores(response);
        if(request.getMethod().equalsIgnoreCase("OPTIONS")){
            response.end();
            return true;
        }
        Log.d(TAG, "receive request [" + request.getMethod() + "] " + request.getPath());
        return false;
    }
    private final HttpServerRequestCallback empty = (request, response) -> response.send("");
    private final HttpServerRequestCallback getVersion = (request, response) -> response.send("v"+mVersionName);
    private final HttpServerRequestCallback getStatic = (request, response) -> {
        if(mAssetManager == null){
            response.code(404);
            response.end();
            return;
        }
        InputStream inputStream = null;
        try {
            mAssetManager.list("index.html");
            String urlPath = request.getPath().substring(1);
            inputStream = mAssetManager.open(urlPath);
        } catch (Exception ignore) {
        }
        if(inputStream == null){
            try {
                inputStream = mAssetManager.open("index.html");
            } catch (Exception ignore) {
            }
        }
        if(inputStream != null){
            try {
                byte[] buffer = new byte[inputStream.available()];
                int ignore = inputStream.read(buffer);
                response.send(getMimeType(request.getPath()), buffer);
                return;
            } catch (Exception ignore) {
            }
        }
        response.code(404);
        response.end();
    };
    public String getMimeType(String filePath)
    {
        String extension = "";
        if(filePath != null){
            String[] paths = filePath.split("\\.");
            if(paths.length > 0) extension = paths[paths.length - 1];
        }
        switch (extension)
        {
            case "html":
                return "text/html";
            case "js":
                return "application/javascript; charset=utf-8";
            case "css":
                return "text/css; charset=utf-8";
            case "woff":
                return "font/woff2";
            case "png":
                return "image/png";
            case "jpg":
                return "image/jpg";
            case "svg":
                return "image/svg+xml";
            case "webp":
                return "image/webp";
        }
        return "text/html";
    }
    private final HttpServerRequestCallback config = (request, response) -> {
        JSONObject jsonObject = new JSONObject();
        try {
            jsonObject.put("remote", true);
        } catch (JSONException e) {
            Log.e(TAG, "set config item error", e);
        }
        response.send(jsonObject);
    };
    private final HttpServerRequestCallback getStorage = (request, response) -> {
        String key = request.getQuery().getString("key");
        if(mPreferences != null && key != null && !key.isEmpty()){
            String value = mPreferences.getString(key, "");
            response.send(value);
        }else {
            response.end();
        }
    };
    private final HttpServerRequestCallback setStorage = (request, response) -> {
        Object obj = request.getBody().get();
        if(obj instanceof JSONObject){
            JSONObject body = (JSONObject) obj;
            try {
                String key = body.getString("key");
                String value = body.getString("value");
                if(mPreferences != null){
                    mPreferences.edit().putString(key, value).apply();
                }
            }catch (Exception ignore){}
        }
        response.end();
    };
    private Timer delayPauseTimer = null;
    private final HttpServerRequestCallback delayExit = (request, response) -> {
        String delayMinuteStr = String.valueOf(request.getBody().get());
        try{
            long delayMinute = Integer.parseInt(delayMinuteStr);
            if(delayPauseTimer != null){
                delayPauseTimer.cancel();
                delayPauseTimer = null;
            }
            if(delayMinute > 0){
                delayPauseTimer = new Timer();
                delayPauseTimer.schedule(new TimerTask() {
                    @Override
                    public void run() {
                        mAudioPlayer.pause();
                    }
                }, delayMinute * 60 *1000);
            }
        }catch (Exception ignore){
            delayPauseTimer = null;
        }
        response.send("");
    };
    private final HttpServerRequestCallback play = (request, response) -> {
        Object body = request.getBody().get();
        if(body == null){
            mAudioPlayer.play();
            response.send(mAudioPlayer.getStatus());
            return;
        }
        if(body instanceof JSONObject){
            mAudioPlayer.play(MusicPlayRequest.of((JSONObject) body));
        }else{
            String url = body.toString();
            if(url.startsWith("http")){
                mAudioPlayer.play(url);
            }else {
                File file = new File(url);
                if (file.exists()){
                    mAudioPlayer.play(url);
                }
            }
        }
        response.send(mAudioPlayer.getStatus());
    };
    private final HttpServerRequestCallback updateList = (request, response) -> {
        Object body = request.getBody().get();
        if(body instanceof JSONObject){
            mAudioPlayer.setMusicPlayRequest(MusicPlayRequest.of((JSONObject) body));
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback pause = (request, response) -> {
        mAudioPlayer.pause();
        response.send(mAudioPlayer.getStatus());
    };
    private final HttpServerRequestCallback progress = (request, response) -> {
        int progress = Integer.parseInt(request.getBody().get().toString());
        mAudioPlayer.setProgress(progress);
        response.send(mAudioPlayer.getStatus());
    };
    private final HttpServerRequestCallback volume = (request, response) -> {
        int volume = Integer.parseInt(request.getBody().get().toString());
        mAudioPlayer.setVolume(volume);
        response.send(mAudioPlayer.getStatus());
    };
    private final HttpServerRequestCallback status = (request, response) -> response.send(mAudioPlayer.getStatus());
    private final HttpServerRequestCallback loop = (request, response) -> {
        String loopType = request.getBody().get().toString().toLowerCase();
        switch (loopType)
        {
            case "single": mAudioPlayer.setLoopType(AudioPlayer.LoopType.Single); break;
            case "random": mAudioPlayer.setLoopType(AudioPlayer.LoopType.Random); break;
            case "order": mAudioPlayer.setLoopType(AudioPlayer.LoopType.Order); break;
            default: mAudioPlayer.setLoopType(AudioPlayer.LoopType.Loop); break;
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback quality = (request, response) -> {
        String qualityType = request.getBody().get().toString().toLowerCase();
        switch (qualityType)
        {
            case "sq": mAudioPlayer.setQuality(MusicItem.Quality.SQ); break;
            case "hq": mAudioPlayer.setQuality(MusicItem.Quality.HQ); break;
            case "zq": mAudioPlayer.setQuality(MusicItem.Quality.ZQ); break;
            default: mAudioPlayer.setQuality(MusicItem.Quality.PQ); break;
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback image = (request, response) -> response.send("");
    private final HttpServerRequestCallback getRemoteClients = (request, response) -> {
        List<Map<String, Object>> clients = new ArrayList<>();
        AsyncNetworkSocket socket = (AsyncNetworkSocket) request.getSocket();
        String localAddress = socket.getLocalAddress().getHostAddress();
        clients.add(RemoteClient.of(mDeviceName, localAddress, mServerPort, mAudioPlayer.getChannel()).toMap());
        for (String key: mRemoteClients.keySet()) {
            RemoteClient client = mRemoteClients.get(key);
            if(client != null) clients.add(client.toMap());
        }
        response.send(new JSONArray(clients));
    };
    private final HttpServerRequestCallback updateRemoteClient = (request, response) -> {
        try {
            JSONObject obj = (JSONObject) request.getBody().get();
            RemoteClient client = RemoteClient.of(obj);
            AsyncNetworkSocket socket = (AsyncNetworkSocket) request.getSocket();
            String localAddress = socket.getLocalAddress().getHostAddress();
            if(localAddress != null && localAddress.equals(client.getAddress())){
                setChannel(client.getChannel());
                response.send("application/json", "{\"data\":false}");
                return;
            }
            if(!mRemoteClients.containsKey(client.getAddress())) {
                response.send("application/json", "{\"data\":false}");
                return;
            }
            RemoteClient localClient = Objects.requireNonNull(mRemoteClients.get(client.getAddress()));
            localClient.setChannel(client.getChannel());
            if(client.getChannel() < 0) {
                localClient.send(RemoteMessage.of(RemoteMessage.MessageTypePause));
                localClient.setChannel(AudioPlayer.ChannelTypeNone);
            }else {
                localClient.send(RemoteMessage.of(RemoteMessage.MessageTypeChannel)
                        .setChannel(client.getChannel()));
                if(mAudioPlayer.isPlaying()){
                    localClient.send(RemoteMessage.of(RemoteMessage.MessageTypePlay)
                            .setUrl(mAudioPlayer.getUrl())
                            .setMusic(mAudioPlayer.getCurrentMusic())
                            .setVolume(mAudioPlayer.getVolume()));
                }
            }
            mPreferences.edit().putInt("channel-"+localClient.getAddress(), localClient.getChannel()).apply();
            response.send("application/json", "{\"data\":true}");
        } catch (Exception ignore) {
            response.send("application/json", "{\"data\":false}");
        }
        response.end();
    };

    //region proxy
    private final HttpServerRequestCallback proxyGet = (request, response) -> {
        String url = request.getQuery().getString("url");
        if(url == null || url.isEmpty()) {
            response.end();
            return;
        }
        try {
            HttpProxy.handle(url, (source, result) -> responseProxyData(response, source, result));
        }catch (Exception e){
            Log.e(TAG, "send proxy get error", e);
            response.end();
        }
    };

    private final HttpServerRequestCallback proxyPost = (request, response) -> {
        Object body = request.getBody().get();
        ProxyRequestData proxyRequestData = ProxyRequestData.of(body);
        String url = proxyRequestData.getUrl();
        if(url == null || url.isEmpty()) {
            response.send("");
            return;
        }
        try {
            HttpProxy.handle(proxyRequestData, (source, result) -> responseProxyData(response, source, result));
        }catch (Exception e){
            Log.e(TAG, "http proxy post error", e);
            response.end();
        }
    };

    private void responseProxyData(AsyncHttpServerResponse response, AsyncHttpResponse responseProxy, ByteBufferList buffer){
        if(responseProxy == null) {
            response.end();
            return;
        }
        Map<String, String> headers = parseHeader(responseProxy.headers());
        if(responseProxy.code() > 300 && responseProxy.code() < 310){
            response.code(200);
            response.setContentType("application/json");
            response.send(new JSONObject(headers));
            response.end();
            return;
        }
        response.code(responseProxy.code());
        setHeader(response, headers);
        try {
            if(buffer != null){
                String contentType = headers.get("content-type");
                if(contentType == null || contentType.isEmpty()){
                    contentType = "text/plain;charset=UTF-8";
                }
                response.send(contentType, buffer);
            }else {
                response.end();
            }
        }catch (Exception e){
            Log.e(TAG, "get proxy buffer error", e);
            response.end();
        }
    }
    //endregion

    //region header
    private Map<String, String> parseHeader(Headers headers){
        Map<String, String> result = new HashMap<>();
        for(String key: headers.getMultiMap().keySet()){
            result.put(key, String.join(",", headers.getAll(key)));
            if(key.equalsIgnoreCase("set-cookie")){
                result.put("Set-Cookie-Renamed", result.get(key));
            }
        }
        return result;
    }

    private void setCores(AsyncHttpServerResponse response){
        Headers headers = response.getHeaders();
        headers.set("Access-Control-Allow-Origin", "*");
        headers.set("Access-Control-Allow-Methods", "*");
        headers.set("Access-Control-Allow-Headers", "*");
        headers.set("Access-Control-Expose-Headers", "*");
        headers.set("Access-Control-Allow-Credentials", "true");
    }

    private void setHeader(AsyncHttpServerResponse response, Map<String, String> headers){
        for(String key: headers.keySet()){
            switch (key.toLowerCase().replaceAll("-", "")){
                case "cookies":
                case "connection":
                case "contentlength":
                case "contentencoding":
                case "transferencoding":
                case "accesscontrolalloworigin":
                case "accesscontrolallowheaders":
                case "accesscontrolallowmethods":
                case "accesscontrolexposeheaders":
                case "accesscontrolallowcredentials":
                    break;
                default:
                    response.getHeaders().set(key, headers.get(key));
                    break;
            }
        }
    }
    //endregion

    //region websocket
    private final AsyncHttpServer.WebSocketRequestCallback websocketHandler = (webSocket, request) -> {
        mWebClients.add(webSocket);
        webSocket.send(mAudioPlayer.getStatus().toString());
        webSocket.setClosedCallback(e -> {
            try {
                if (e != null) Log.e(TAG, "An error occurred", e);
            } finally {
                mWebClients.remove(webSocket);
            }
        });
    };

    private final AsyncHttpServer.WebSocketRequestCallback serverWebsocketHandler = (webSocket, request) -> this.onRemoteServerSocket(webSocket);

    public void sendWSMessage(String message){
        for (WebSocket socket: mWebClients) {
            try {
                socket.send(message);
            }catch (Exception e){
                Log.e(TAG, "send websocket msg error", e);
            }
        }
    }
    public void sendServerWSMessage(RemoteMessage message){
//        Log.d(TAG, "send remote server message: " + message.toJson());
        for (String key: mRemoteClients.keySet()) {
            try {
                Objects.requireNonNull(mRemoteClients.get(key)).send(message);
            }catch (Exception e){
                Log.e(TAG, "send websocket msg error", e);
            }
        }
    }
    //endregion
    //endregion
}
