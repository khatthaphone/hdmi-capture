package com.example.hdmicaptain.billing

/** What HDMI Captain Pro unlocks (docs/adr/0004-monetization.md). Free features never check this. */
enum class ProFeature {
    Recording, ReplayBuffer, Streaming, VirtualCamera;

    companion object {
        /** Not offered on Android yet, as on Apple. */
        val hidden = setOf(Streaming, VirtualCamera)
    }
}
