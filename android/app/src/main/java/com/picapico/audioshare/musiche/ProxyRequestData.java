package com.picapico.audioshare.musiche;

import org.json.JSONObject;

import java.util.HashMap;
import java.util.Iterator;
import java.util.Map;

public class ProxyRequestData {
    private String url = "";
    private String method = "";
    private String data = "";
    private final Map<String, String> headers = new HashMap<>();
    private boolean allowAutoRedirect = true;

    public static ProxyRequestData of(Object json){
        try{
            JSONObject jsonObject = (JSONObject) json;
            ProxyRequestData proxyRequestData = new ProxyRequestData();
            if(jsonObject.has("url")){
                proxyRequestData.setUrl(jsonObject.getString("url"));
            }
            if(jsonObject.has("method")){
                proxyRequestData.setMethod(jsonObject.getString("method"));
            }
            if(jsonObject.has("data")){
                proxyRequestData.setData(jsonObject.getString("data"));
            }
            if(jsonObject.has("allowAutoRedirect")){
                proxyRequestData.setAllowAutoRedirect(jsonObject.getBoolean("allowAutoRedirect"));
            }
            if(jsonObject.has("headers")){
                JSONObject headersJson = jsonObject.getJSONObject("headers");
                for (Iterator<String> it = headersJson.keys(); it.hasNext(); ) {
                    String key = it.next();
                    proxyRequestData.getHeaders().put(key, headersJson.get(key).toString());
                }
            }
            return proxyRequestData;
        }catch (Exception ignore){
            return new ProxyRequestData();
        }
    }

    public boolean hasBody(){
        return data != null && !data.isEmpty();
    }

    public String getUrl() {
        return url;
    }

    public void setUrl(String url) {
        this.url = url;
    }

    public String getMethod() {
        if(method == null || method.isEmpty()) return "get";
        return method.toUpperCase();
    }

    public void setMethod(String method) {
        this.method = method;
    }

    public String getData() {
        return data;
    }

    public void setData(String data) {
        this.data = data;
    }

    public Map<String, String> getHeaders() {
        return headers;
    }

    public boolean isAllowAutoRedirect() {
        return allowAutoRedirect;
    }

    public void setAllowAutoRedirect(boolean allowAutoRedirect) {
        this.allowAutoRedirect = allowAutoRedirect;
    }
}
