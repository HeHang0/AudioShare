package com.picapico.audioshare.musiche;

import android.util.Log;

import com.picapico.audioshare.NetworkUtils;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.util.List;

public class BroadcastReceiver {
    private static final String TAG = "AudioShareBCReceiver";
    private static final String HEAD = "picapico-audio-share";
    public interface OnRemoteServerReceivedListener {
        void OnRemoteServerReceived(String hostname, String port);
    }
    private OnRemoteServerReceivedListener remoteServerReceivedListener = null;

    public void setRemoteServerReceivedListener(OnRemoteServerReceivedListener listener){
        remoteServerReceivedListener = listener;
    }
    private DatagramSocket socket = null;
    private boolean ignoreError = false;
    private final String localAddresses;

    public BroadcastReceiver(){
        localAddresses = getIPV4();
    }

    public void start(){
        if(socket != null) return;
        new Thread(this::startReceiveBroadcast).start();
    }

    private void startReceiveBroadcast(){
        for (int port = 58261; port < 58271; port++) {
            try{
                socket = new DatagramSocket(port);
                socket.setBroadcast(true);
                break;
            }catch (Exception ignore){ }
        }
        if(socket == null) {
            Log.w(TAG, "cannot listen udp");
            return;
        }
        byte[] buffer = new byte[128];
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        try{
            while (true) {
                socket.receive(packet);
                if(remoteServerReceivedListener == null) continue;
                InetAddress packageAddress = packet.getAddress();
                if(packageAddress == null || packageAddress.getHostAddress() == null
                        || localAddresses.contains(packageAddress.getHostAddress())){
                    continue;
                }
                String[] messages = new String(packet.getData(), 0, packet.getLength()).split("@");
                if(messages.length < 3 || !messages[0].startsWith(HEAD)) continue;
                remoteServerReceivedListener.OnRemoteServerReceived(packet.getAddress().getHostAddress(), messages[2]);
            }
        }catch (Exception e){
            if(!ignoreError) Log.e(TAG, "cannot receive udp: " + e.getMessage());
        }
        stop();
        ignoreError = false;
    }

    public void stop(){
        ignoreError = true;
        if(socket != null){
            try {
                socket.close();
            } catch (Exception ignore) { }
        }
        socket = null;
    }

    private String getIPV4(){
        StringBuilder addressesSB = new StringBuilder();
        List<InetAddress> allAddresses = NetworkUtils.getAllInetAddress();
        for (int i = 0; i < allAddresses.size();i++) {
            if(allAddresses.get(i) instanceof Inet4Address){
                addressesSB.append("-").append(allAddresses.get(i).getHostAddress()).append("-");
            }
        }
        return addressesSB.toString();
    }
}
