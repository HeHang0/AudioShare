package com.picapico.audioshare;

import android.content.Context;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.NetworkCapabilities;
import android.net.NetworkInfo;
import android.net.wifi.WifiInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.util.Log;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.ServerSocket;
import java.net.SocketException;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.List;

public class NetworkUtils {
    private static final String TAG = "AudioShareNetworkUtils";
    public static final String BROADCAST_ADDRESS = "255.255.255.255";
    public static final String BROADCAST_ADDRESS_V6 = "FF02::1";
    public static boolean checkPortBusy(int port){
        try {
            new ServerSocket(port).close();
            return false;
        } catch (Exception ignore) {
            return true;
        }
    }

    public static int getFreePort(){
        return getFreePort(8088);
    }

    public static int getFreePort(int port){
        for (; port < 65535; port++) {
            if(!checkPortBusy(port)){
                break;
            }
        }
        return port;
    }

    public static String getIpAddress(Context context){
        try{

            NetworkInfo info = ((ConnectivityManager) context
                    .getSystemService(Context.CONNECTIVITY_SERVICE)).getActiveNetworkInfo();
            if (info != null && isNetworkAvailable(context, info)) {
                if (info.getType() == ConnectivityManager.TYPE_ETHERNET){
                    return getLocalIp();
                } else if (info.getType() == ConnectivityManager.TYPE_WIFI) {
                    WifiManager wifiManager = (WifiManager) context.getApplicationContext()
                            .getSystemService(Context.WIFI_SERVICE);
                    WifiInfo wifiInfo = wifiManager.getConnectionInfo();
                    return intIP2StringIP(wifiInfo.getIpAddress());
                }
            }
        }catch (Exception e){
            Log.e(TAG, "get ip address error: " + e);
        }
        return "";
    }

    private static Boolean isNetworkAvailable(Context context, NetworkInfo info) {
        ConnectivityManager connectivityManager =
                (ConnectivityManager) context.getSystemService(Context.CONNECTIVITY_SERVICE);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            Network nw = connectivityManager.getActiveNetwork();
            if (nw == null) return false;
            NetworkCapabilities actNw = connectivityManager.getNetworkCapabilities(nw);
            return actNw != null && (actNw.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) ||
                    actNw.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) ||
                    actNw.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) ||
                    actNw.hasTransport(NetworkCapabilities.TRANSPORT_BLUETOOTH));
        } else {
            return info != null && info.isConnected();
        }
    }

    private static String intIP2StringIP(int ip) {
        return (ip & 0xFF) + "." +
                ((ip >> 8) & 0xFF) + "." +
                ((ip >> 16) & 0xFF) + "." +
                (ip >> 24 & 0xFF);
    }


    // 获取有限网IP
    public static String getLocalIp() {
        try {
            Enumeration<NetworkInterface> en = NetworkInterface
                .getNetworkInterfaces();
            while (en.hasMoreElements()) {
                NetworkInterface networkInterface = en.nextElement();
                for (Enumeration<InetAddress> enumIpAddress = networkInterface
                        .getInetAddresses(); enumIpAddress.hasMoreElements(); ) {
                    InetAddress inetAddress = enumIpAddress.nextElement();
                    if (!inetAddress.isLoopbackAddress()
                            && inetAddress instanceof Inet4Address) {
                        return inetAddress.getHostAddress();
                    }
                }
            }
        } catch (SocketException ex) {
            Log.e(TAG, "get local ip error: " + ex);
        }
        return "0.0.0.0";
    }

    public static List<InetAddress> getAllInetAddress(){
        List<InetAddress> localAddresses = new ArrayList<>();
        Enumeration<NetworkInterface> interfaces;
        try {
            interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces.hasMoreElements()) {
                try {
                    NetworkInterface networkInterface = interfaces.nextElement();
                    Enumeration<InetAddress> addresses = networkInterface.getInetAddresses();
                    byte[] hardwareAddress = networkInterface.getHardwareAddress();
                    String name = networkInterface.getName();
                    if((hardwareAddress == null &&
                            !name.startsWith("eth0") &&
                            !name.startsWith("wlan0")) || name.startsWith("dummy")) {
                        continue;
                    }
                    boolean isLoopback = networkInterface.isLoopback();
                    boolean isUp = networkInterface.isUp();
                    if(isLoopback || !isUp) continue;
                    while (addresses.hasMoreElements()) {
                        InetAddress element = addresses.nextElement();
                        if(element.isLinkLocalAddress()) continue;
                        Log.i(TAG, String.format("name: %s, loopback: %b, up: %b, linkLocal: %b, address: %s", name, isLoopback, isUp, element.isLinkLocalAddress(), element.toString()));
                        localAddresses.add(element);
                    }
                } catch (SocketException e) {
                    Log.e(TAG, "get all inet address error: " + e);
                }
            }
        } catch (SocketException e) {
            Log.e(TAG, "get all inet address error: " + e);
        }
        return localAddresses;
    }
}
