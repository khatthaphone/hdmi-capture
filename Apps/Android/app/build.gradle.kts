import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.compose)
}

android {
    namespace = "com.example.hdmicaptain"
    compileSdk = libs.versions.compileSdk.get().toInt()
    ndkVersion = libs.versions.ndk.get()

    defaultConfig {
        applicationId = "com.example.hdmicaptain"
        minSdk = libs.versions.minSdk.get().toInt()
        targetSdk = libs.versions.targetSdk.get().toInt()
        versionCode = 1
        versionName = "0.1"
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }

    // Upload key lives outside the repo (Play App Signing holds the app signing key):
    // ~/.config/hdmicaptain/android-signing.properties with storeFile, storePassword, keyAlias, keyPassword.
    val signing = File(System.getProperty("user.home"), ".config/hdmicaptain/android-signing.properties")
    if (signing.exists()) {
        val props = Properties().apply { signing.inputStream().use(::load) }
        signingConfigs.create("upload") {
            storeFile = File(props.getProperty("storeFile"))
            storePassword = props.getProperty("storePassword")
            keyAlias = props.getProperty("keyAlias")
            keyPassword = props.getProperty("keyPassword")
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("upload")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    packaging { jniLibs { useLegacyPackaging = false } }
}

kotlin { jvmToolchain(21) }

dependencies {
    implementation(project(":feature:viewer"))
    implementation(project(":feature:settings"))
    implementation(project(":core:capture"))
    implementation(project(":core:render"))
    implementation(project(":core:audio"))
    implementation(project(":core:billing"))
    implementation(project(":core:prefs"))
    implementation(project(":core:record"))
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.profileinstaller)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.kotlinx.coroutines.android)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.material3)
}
