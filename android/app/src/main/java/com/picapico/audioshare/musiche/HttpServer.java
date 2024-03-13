package com.picapico.audioshare.musiche;

import android.content.Context;
import android.content.SharedPreferences;
import android.content.res.AssetManager;
import android.media.AudioManager;
import android.util.Log;

import com.koushikdutta.async.ByteBufferList;
import com.koushikdutta.async.http.AsyncHttpResponse;
import com.koushikdutta.async.http.Headers;
import com.koushikdutta.async.http.WebSocket;
import com.koushikdutta.async.http.server.AsyncHttpServer;
import com.koushikdutta.async.http.server.AsyncHttpServerRequest;
import com.koushikdutta.async.http.server.AsyncHttpServerResponse;
import com.koushikdutta.async.http.server.HttpServerRequestCallback;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.InputStream;
import java.util.ArrayList;
import java.util.HashMap;
import java.util.List;
import java.util.Map;
import java.util.Timer;
import java.util.TimerTask;

public class HttpServer {
    private static final String TAG = "AudioShareHttpServer";
    private String versionName = "";
    AsyncHttpServer server = new AsyncHttpServer(){

        @Override
        public boolean onRequest(AsyncHttpServerRequest request, AsyncHttpServerResponse response) {
            Log.d(TAG, "receive request [" + request.getMethod() + "] " + request.getPath());
            setCores(response);
            if(request.getMethod().equalsIgnoreCase("OPTIONS")){
                response.end();
                return true;
            }
            return false;
        }
    };
    AudioPlayer audioPlayer;
    List<WebSocket> _sockets = new ArrayList<>();
    SharedPreferences mPreferences;
    AssetManager mAssetManager;
    public HttpServer(Context context, int port) {
        try {
            audioPlayer = new AudioPlayer(context);
            server.listen(port);
            Log.i(TAG, "start server on: " + port);
        }catch (Exception e){
            Log.e(TAG, "start server error: ", e);
        }
        initHandler();
    }

    public void setVersionName(String versionName){
        this.versionName = versionName;
    }

    public void setSharedPreferences(SharedPreferences preferences){
        this.mPreferences = preferences;
    }

    public void setAssetManager(AssetManager assetManager){
        this.mAssetManager = assetManager;
    }

    public void stop(){
        server.stop();
    }

    public void setAudioManager(AudioManager audioManager){
        audioPlayer.setAudioManager(audioManager);
        audioPlayer.setOnLoadSuccessListener(this::onMediaPositionChanged);
    }

    public void onMediaPositionChanged() {
        sendWSMessage(audioPlayer.getStatus().toString());
    }

    public AudioPlayer getAudioPlayer() {
        return audioPlayer;
    }

    private void initHandler(){
        server.websocket("/ws", websocketHandler);
        server.get("/version", getVersion);
        server.post("/version", getVersion);
        server.get("/config", config);
        server.post("/config", config);
        server.get("/storage", getStorage);
        server.post("/storage", setStorage);
        server.post("/title", empty);
        server.post("/media", empty);
        server.post("/fadein", empty);
        server.post("/delayExit", delayExit);
        server.post("/gpu", empty);
        server.post("/play", play);
        server.post("/updatelist", updateList);
        server.post("/pause", pause);
        server.post("/progress", progress);
        server.post("/volume", volume);
        server.post("/status", status);
        server.post("/window", empty);
        server.post("/maximize", empty);
        server.post("/minimize", empty);
        server.post("/loop", loop);
        server.post("/quality", quality);
        server.post("/exit", pause);
        server.post("/hide", empty);
        server.post("/minimize", empty);
        server.post("/fonts", empty);
        server.post("/image", image);
        server.post("/theme", empty);
        server.post("/lyric", empty);
        server.post("/lyricline", empty);
        server.post("/hotkey", empty);
        server.post("/proxy", proxyPost);
        server.get("/proxy", proxyGet);
        server.get(".*", getStatic);
//        server.addAction("OPTIONS",".*", options);
    }
    private final HttpServerRequestCallback empty = (request, response) -> response.send("哈哈");
    private final HttpServerRequestCallback getVersion = (request, response) -> response.send(versionName);
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
                inputStream.read(buffer);
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
                        audioPlayer.pause();
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
            audioPlayer.play();
            response.send(audioPlayer.getStatus());
            return;
        }
        if(body instanceof JSONObject){
            audioPlayer.play(MusicPlayRequest.of((JSONObject) body));
        }else{
            String url = body.toString();
            if(url.startsWith("http")){
                audioPlayer.play(url);
            }else {
                File file = new File(url);
                if (file.exists()){
                    audioPlayer.play(url);
                }
            }
        }
        response.send(audioPlayer.getStatus());
    };
    private final HttpServerRequestCallback updateList = (request, response) -> {
        Object body = request.getBody().get();
        if(body instanceof JSONObject){
            audioPlayer.setMusicPlayRequest(MusicPlayRequest.of((JSONObject) body));
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback pause = (request, response) -> {
        audioPlayer.pause();
        response.send(audioPlayer.getStatus());
    };
    private final HttpServerRequestCallback progress = (request, response) -> {
        int progress = Integer.parseInt(request.getBody().get().toString());
        audioPlayer.setProgress(progress);
        response.send(audioPlayer.getStatus());
    };
    private final HttpServerRequestCallback volume = (request, response) -> {
        int volume = Integer.parseInt(request.getBody().get().toString());
        audioPlayer.setVolume(volume);
        response.send(audioPlayer.getStatus());
    };
    private final HttpServerRequestCallback status = (request, response) -> response.send(audioPlayer.getStatus());
    private final HttpServerRequestCallback loop = (request, response) -> {
        String loopType = request.getBody().get().toString().toLowerCase();
        switch (loopType)
        {
            case "single": audioPlayer.setLoopType(AudioPlayer.LoopType.Single); break;
            case "random": audioPlayer.setLoopType(AudioPlayer.LoopType.Random); break;
            case "order": audioPlayer.setLoopType(AudioPlayer.LoopType.Order); break;
            default: audioPlayer.setLoopType(AudioPlayer.LoopType.Loop); break;
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback quality = (request, response) -> {
        String qualityType = request.getBody().get().toString().toLowerCase();
        switch (qualityType)
        {
            case "sq": audioPlayer.setQuality(MusicItem.Quality.SQ); break;
            case "hq": audioPlayer.setQuality(MusicItem.Quality.HQ); break;
            case "zq": audioPlayer.setQuality(MusicItem.Quality.ZQ); break;
            default: audioPlayer.setQuality(MusicItem.Quality.PQ); break;
        }
        response.send("application/json", "{\"data\":true}");
    };
    private final HttpServerRequestCallback image = (request, response) -> response.send("");
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

    private final AsyncHttpServer.WebSocketRequestCallback websocketHandler = (webSocket, request) -> {
        _sockets.add(webSocket);
        webSocket.send(audioPlayer.getStatus().toString());
        webSocket.setClosedCallback(e -> {
            try {
                if (e != null) Log.e(TAG, "An error occurred", e);
            } finally {
                _sockets.remove(webSocket);
            }
        });
    };

    public void sendWSMessage(String message){
        for (WebSocket socket: _sockets) {
            try {
                socket.send(message);
            }catch (Exception e){
                Log.e(TAG, "send websocket msg error", e);
            }
        }
    }
}
