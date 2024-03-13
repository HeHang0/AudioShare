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
        AsyncHttpRequest requestProxy = new AsyncHttpRequest(Uri.parse(requestData.getUrl()), requestData.getMethod());
        requestProxy.setFollowRedirect(requestData.isAllowAutoRedirect());
        boolean userAgentSet = false;
        for (String key: requestData.getHeaders().keySet()) {
            switch (key.toLowerCase().replaceAll("-", "")){
                case "useragent":
                    userAgentSet = true;
                    requestProxy.setHeader("User-Agent", requestData.getHeaders().get(key));
                    break;
                case "contenttype": requestProxy.setHeader("Content-Type", requestData.getHeaders().get(key));break;
                default: requestProxy.setHeader(key, requestData.getHeaders().get(key));
            }

        }
        if(!userAgentSet) requestProxy.setHeader("User-Agent", UserAgent);
        if(requestData.hasBody()){
            requestProxy.setBody(new StringBody(requestData.getData()));
        }
        AsyncHttpClient.getDefaultInstance().executeByteBufferList(requestProxy, new AsyncHttpClient.DownloadCallback() {
            @Override
            public void onCompleted(Exception e, AsyncHttpResponse source, ByteBufferList result) {
                if(e != null){
                    Log.e(TAG, "send proxy post error", e);
                }
                listener.onCompleted(source, result);
            }
        });
    }
}
