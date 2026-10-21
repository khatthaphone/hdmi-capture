// Native renderer (EGL/GLES 3) and, later, MJPEG decode. Built with the NDK through CMake.
plugins {
    alias(libs.plugins.android.library)
}

android {
    namespace = "com.example.hdmicaptain.render"
    compileSdk = libs.versions.compileSdk.get().toInt()
    ndkVersion = libs.versions.ndk.get()
    defaultConfig {
        minSdk = libs.versions.minSdk.get().toInt()
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
        externalNativeBuild { cmake { arguments += "-DANDROID_STL=c++_shared" } }
    }
    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }
}

kotlin { jvmToolchain(21) }

dependencies {
    api(project(":core:model"))
}
