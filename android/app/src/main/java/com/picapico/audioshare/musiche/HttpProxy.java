package com.picapico.audioshare.musiche;

import android.net.Uri;
import android.util.Log;

import com.koushikdutta.async.ByteBufferList;
import com.koushikdutta.async.http.AsyncHttpClient;
import com.koushikdutta.async.http.AsyncHttpRequest;
import com.koushikdutta.async.http.AsyncHttpResponse;
import com.koushikdutta.async.http.body.StringBody;

public class HttpProxy {
    private static final String TAG = "AudioShareHttpProxy";
    private static final String UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/117.0.0.0 Safari/537.36";

    public interface OnCompletedListener {
        void onCompleted(AsyncHttpResponse source, ByteBufferList result);
    }
    public static void handle(String url, OnCompletedListener listener){
        ProxyRequestData proxyRequestData = new ProxyRequestData();
        proxyRequestData.setUrl(url);
        handle(proxyRequestData, listener);
    }
    public static void handle(ProxyRequestData requestData, OnCompletedListener listener){
        handle(requestData, listener, true);
    }
    private static AsyncHttpRequest getRequest(ProxyRequestData requestData){
        AsyncHttpRequest request = new AsyncHttpRequest(Uri.parse(requestData.getUrl()), requestData.getMethod());
        request.setFollowRedirect(requestData.isAllowAutoRedirect());
        boolean userAgentSet = false;
        for (String key: requestData.getHeaders().keySet()) {
            switch (key.toLowerCase().replaceAll("-", "")){
                case "useragent":
                    userAgentSet = true;
                    request.setHeader("User-Agent", requestData.getHeaders().get(key));
                    break;
                case "contenttype": request.setHeader("Content-Type", requestData.getHeaders().get(key));break;
                default: request.setHeader(key, requestData.getHeaders().get(key));
            }

        }
        if(!userAgentSet) request.setHeader("User-Agent", UserAgent);
        if(requestData.hasBody()){
            request.setBody(new StringBody(requestData.getData()));
        }
        return request;
    }
    public static void handle(ProxyRequestData requestData, OnCompletedListener listener, boolean retry){
        AsyncHttpRequest request = getRequest(requestData);
        AsyncHttpClient.getDefaultInstance().executeByteBufferList(request, new AsyncHttpClient.DownloadCallback() {
            @Override
            public void onCompleted(Exception e, AsyncHttpResponse source, ByteBufferList result) {
                if(e != null && retry){
                    handle(requestData, listener, false);
                }else {
                    if(e != null){
                        Log.e(TAG, requestData.getMethod().toLowerCase() + " proxy send error", e);
                    }
                    listener.onCompleted(source, result);
                }
            }
        });
    }
}
