plugins {
    id("com.android.application")
}

val isolatedBuildRoot = providers.environmentVariable("NETLIFT_TRUST_TMPDIR")
    .orNull
    ?: throw GradleException("NETLIFT_TRUST_TMPDIR is required for isolated spike builds.")
layout.buildDirectory.set(file("$isolatedBuildRoot/android-package-identity-build"))

android {
    namespace = "com.netlift.spike.androidpackageidentity"
    compileSdk = 36

    lint {
        checkReleaseBuilds = false
    }

    defaultConfig {
        applicationId = "com.netlift.spike.androidpackageidentity"
        minSdk = 28
        targetSdk = 36
        versionCode = 1
        versionName = "1.0"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}
