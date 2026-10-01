plugins {
    id("com.android.application")
}

val isolatedBuildRoot = providers.environmentVariable("NETLIFT_TRUST_TMPDIR")
    .orNull
    ?: throw GradleException("NETLIFT_TRUST_TMPDIR is required for isolated spike builds.")
layout.buildDirectory.set(file("$isolatedBuildRoot/android-build"))

android {
    namespace = "com.netlift.moduleassettrust"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "com.netlift.moduleassettrust.probe"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"

        ndk {
            abiFilters += "arm64-v8a"
        }

    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.30.3"
            buildStagingDirectory = file("$isolatedBuildRoot/android-native-build")
        }
    }

    packaging {
        jniLibs {
            useLegacyPackaging = true
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
