import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "0.1.0-alpha.12"
}

netlift {
    managedProject.set(
        rootProject.file("../HelloWorld.Contracts/HelloWorld.Contracts.csproj")
    )
    contractRoots.set(listOf("HelloWorld.Contracts.IHelloService"))
    kotlinPackage.set("dev.netlift.helloworld")
    componentApplicationId.set("dev.netlift.helloworld.component")
}

android {
    namespace = "dev.netlift.helloworld"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "dev.netlift.helloworld"
        minSdk = 24
        targetSdk = 36
        ndk {
            abiFilters += "arm64-v8a"
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
