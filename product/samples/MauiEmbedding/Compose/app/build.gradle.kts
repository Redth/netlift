plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
    id("io.github.redth.netlift") version "0.1.0-alpha.14"
}

netlift {
    managedProject.set(rootProject.file("../Managed/MauiEmbedding.csproj"))
    contractRoots.set(listOf("NetLift.MauiEmbedding.IEmbeddingService"))
    platformViewFactory.set("NetLift.MauiEmbedding.PlatformViews")
    kotlinPackage.set("dev.netlift.embedding")
    componentApplicationId.set("dev.netlift.maui.component")
}

android {
    namespace = "dev.netlift.maui.compose"
    compileSdk = 36
    defaultConfig {
        applicationId = "dev.netlift.maui.compose"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "0.1"
        ndk { abiFilters += "arm64-v8a" }
    }
    buildFeatures { compose = true }
    packaging { jniLibs.useLegacyPackaging = true }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin { jvmToolchain(17) }

dependencies {
    implementation(platform("androidx.compose:compose-bom:2025.08.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.security:security-crypto:1.1.0-alpha07")
    implementation("com.google.android.material:material:1.14.0")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.material3:material3")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
