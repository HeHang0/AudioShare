package com.picapico.audioshare.musiche;

import android.util.Base64;
import android.util.Log;

import com.koushikdutta.async.http.AsyncHttpClient;
import com.koushikdutta.async.http.AsyncHttpPost;
import com.koushikdutta.async.http.AsyncHttpRequest;
import com.koushikdutta.async.http.AsyncHttpResponse;
import com.koushikdutta.async.http.body.StringBody;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.UnsupportedEncodingException;
import java.net.URLEncoder;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

import javax.crypto.Cipher;
import javax.crypto.spec.IvParameterSpec;
import javax.crypto.spec.SecretKeySpec;

public class MusicItem {
    private static final String TAG = "AudioShareMusicItem";
    private static final String EncSecKey = "&encSecKey=409afd10f2fa06173df57525287c4a1cdf6fa08bd542c6400da953704eb92dc1ad3c582e82f51a707ebfa0f6a25bcd185139fc1509d40dd97b180ed21641df55e90af4884a0b587bd25256141a9270b1b6f18908c6a626b74167e5a55a796c0f808a2eb12c33e63d34a7c4d358bab1dc661637dd1e888a1268b81a89f6136053";
    private static final String CloudMusicAPI = "https://music.163.com/weapi/song/enhance/player/url?csrf_token=";
    private static final String UserAgent = "Mozilla/5.0 (iPad; CPU OS 11_0 like Mac OS X) AppleWebKit/604.1.34 (KHTML, like Gecko) Version/11.0 Mobile/15A5341f Safari/604.1";
    private String id;
    private String name;
    private String image;
//    private String mediumImage;
//    private String largeImage;
    private String singer;
    private String album;
    private String remark;
    private String url;
    private String cookie;
    private String musicU = "";
    private String csrf = "";
    private String type;
    private boolean audition;
    public interface onUrlLoadedCallback {
        void onUrlLoaded(String url);
    }
    public void getRemoteUrl(onUrlLoadedCallback callback){
        switch (type){
            case "cloud": getCloudMusicUrl(callback);break;
            case "qq": getQQMusicUrl(callback);break;
            case "migu": getMiGuMusicUrl(callback);break;
            default:
                callback.onUrlLoaded(null);break;
        }
    }
    private void getCloudMusicUrl(onUrlLoadedCallback callback){
        int br = 320000;
        Map<String, Object> data = new HashMap<>();
        data.put("ids", new String[]{id});
        data.put("br", br);
        data.put("csrf_token", csrf);
        String param = aesEncrypt(new JSONObject(data).toString(), "0CoJUm6Qyw8W8jud");
        param = aesEncrypt(param, "t9Y0m4pdsoMznMlL");
        try {
            param = URLEncoder.encode(param, "UTF-8");
        } catch (UnsupportedEncodingException ignore) {
        }
        String paramData = "params=" + param + EncSecKey;
        try {
            AsyncHttpRequest request = new AsyncHttpPost(CloudMusicAPI);
            request.setHeader("Content-Type", "'application/x-www-form-urlencoded'");
            request.setHeader("Referer", "https://music.163.com");
            request.setHeader("User-Agent", UserAgent);
            request.setHeader("Cookie", "os=ios;MUSIC_U="+musicU);
            request.setBody(new StringBody(paramData));
            AsyncHttpClient.getDefaultInstance().executeJSONObject(request, new AsyncHttpClient.JSONObjectCallback() {
                @Override
                public void onCompleted(Exception e, AsyncHttpResponse source, JSONObject result) {
                    String url = null;
                    if(result != null && result.has("data")){
                        try {
                            JSONObject obj = result.getJSONArray("data").getJSONObject(0);
                            if(obj.has("url")) {
                                url = obj.getString("url").replace("http://", "https://");
                            }
                        } catch (JSONException ex) {
                            Log.e(TAG, "get cloud url error", ex);
                        }
                    }
                    callback.onUrlLoaded(url);
                }
            });
        }catch (Exception e){
            Log.e(TAG, "get cloud url error", e);
            callback.onUrlLoaded(null);
        }
    }
    private void getQQMusicUrl(onUrlLoadedCallback callback){
        callback.onUrlLoaded(null);
    }
    private void getMiGuMusicUrl(onUrlLoadedCallback callback){
        callback.onUrlLoaded(null);
    }
    private String aesEncrypt(String plain, String key){
        String iv = "0102030405060708";
        if (plain == null || key == null) return "";
        try {
            IvParameterSpec ivParameterSpec = new IvParameterSpec(iv.getBytes(StandardCharsets.UTF_8));
            Cipher cipher = Cipher.getInstance("AES/CBC/PKCS5Padding");
            cipher.init(Cipher.ENCRYPT_MODE, new SecretKeySpec(key.getBytes(StandardCharsets.UTF_8), "AES"), ivParameterSpec);
            String base64Str = Base64.encodeToString(cipher.doFinal(plain.getBytes(StandardCharsets.UTF_8)), Base64.DEFAULT);
            return base64Str.replaceAll("\n", "").replaceAll("\r", "");
        } catch (Exception e) {
            e.printStackTrace();
        }
        return "";
    }
    public static MusicItem of(JSONObject jsonObject){
        MusicItem musicItem = new MusicItem();
        try {
            if(jsonObject.has("id")){
                musicItem.setId(jsonObject.getString("id"));
            }
            if(jsonObject.has("name")){
                musicItem.setName(jsonObject.getString("name"));
            }
            if(jsonObject.has("largeImage")){
                musicItem.setImage(jsonObject.getString("largeImage"));
            }else if(jsonObject.has("mediumImage")){
                musicItem.setImage(jsonObject.getString("mediumImage"));
            }else if(jsonObject.has("image")){
                musicItem.setImage(jsonObject.getString("image"));
            }
            if(jsonObject.has("singer")){
                musicItem.setSinger(jsonObject.getString("singer"));
            }
            if(jsonObject.has("album")){
                musicItem.setAlbum(jsonObject.getString("album"));
            }
            if(jsonObject.has("remark")){
                musicItem.setRemark(jsonObject.getString("remark"));
            }
            if(jsonObject.has("audition")){
                musicItem.setAudition(jsonObject.getBoolean("audition"));
            }
//            if(jsonObject.has("url")){
//                musicItem.setUrl(jsonObject.getString("url"));
//            }
            if(jsonObject.has("type")){
                musicItem.setType(jsonObject.getString("type"));
            }
            if(jsonObject.has("cookie")){
                if("cloud".equals(musicItem.getType())){
                    JSONObject typeObj = jsonObject.getJSONObject("cookie");
                    if(typeObj.has("__csrf")){
                        musicItem.setCsrf(typeObj.getString("__csrf"));
                    }
                    if(typeObj.has("MUSIC_U")){
                        musicItem.setMusicU(typeObj.getString("MUSIC_U"));
                    }
                }else {
                    musicItem.setCookie(jsonObject.getString("cookie"));
                }
            }
        } catch (Exception e) {
            Log.e(TAG, "parse music item error", e);
        }
        return musicItem;
    }

    public String getUrl() {
        return url;
    }

    public void setUrl(String url) {
        this.url = url;
    }

    public String getId() {
        return id;
    }

    public void setId(String id) {
        this.id = id;
    }

    public String getName() {
        return name;
    }

    public void setName(String name) {
        this.name = name;
    }

    public String getImage() {
        return image;
    }

    public void setImage(String image) {
        this.image = image;
    }

    public String getSinger() {
        return singer;
    }

    public void setSinger(String singer) {
        this.singer = singer;
    }

    public String getAlbum() {
        return album;
    }

    public void setAlbum(String album) {
        this.album = album;
    }

    public String getRemark() {
        return remark;
    }

    public void setRemark(String remark) {
        this.remark = remark;
    }

    public boolean isAudition() {
        return audition;
    }

    public void setAudition(boolean audition) {
        this.audition = audition;
    }

    public String getCookie() {
        return cookie;
    }

    public void setCookie(String cookie) {
        this.cookie = cookie;
    }

    public String getType() {
        return type;
    }

    public void setType(String type) {
        this.type = type;
    }

    public void setMusicU(String musicU) {
        this.musicU = musicU;
    }

    public void setCsrf(String csrf) {
        this.csrf = csrf;
    }
}
