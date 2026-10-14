import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "0.1.0-alpha.15"
}

val netliftReleaseStoreFile = providers.gradleProperty("netliftReleaseStoreFile")
val netliftReleaseStorePassword = providers.gradleProperty("netliftReleaseStorePassword")
val netliftReleaseKeyAlias = providers.gradleProperty("netliftReleaseKeyAlias")
val netliftReleaseKeyPassword = providers.gradleProperty("netliftReleaseKeyPassword")

netlift {
    managedProject.set(
        rootProject.file(
            "../Orders.Contracts/Orders.Contracts.csproj"
        )
    )
    contractRoots.set(listOf("Orders.Contracts.IOrdersService"))
    kotlinPackage.set("dev.netlift.orders")
    swiftModule.set("OrdersNetLift")
    componentApplicationId.set("dev.netlift.workloadlibrarymode.component")
    compatibilityLock.set(
        rootProject.file("../../contracts/orders.netlift.lock.json")
    )
}

android {
    namespace = "dev.netlift.workloadlibrarymode.app"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    signingConfigs {
        if (
            netliftReleaseStoreFile.isPresent &&
            netliftReleaseStorePassword.isPresent &&
            netliftReleaseKeyAlias.isPresent &&
            netliftReleaseKeyPassword.isPresent
        ) {
            create("netliftRelease") {
                storeFile = rootProject.file(netliftReleaseStoreFile.get())
                storePassword = netliftReleaseStorePassword.get()
                keyAlias = netliftReleaseKeyAlias.get()
                keyPassword = netliftReleaseKeyPassword.get()
            }
        }
    }

    defaultConfig {
        applicationId = "dev.netlift.workloadlibrarymode.app"
        minSdk = 24
        targetSdk = 36
        versionCode = providers.gradleProperty("netliftVersionCode")
            .map(String::toInt)
            .getOrElse(1)
        versionName = providers.gradleProperty("netliftVersionName").getOrElse("0.1")
        ndk {
            abiFilters += "arm64-v8a"
            debugSymbolLevel = "FULL"
        }

        if (providers.gradleProperty("netliftNativeContractRoots").isPresent) {
            sourceSets["main"].java.srcDir("src/callback/java")
        }

    }

    buildTypes {
        release {
            isMinifyEnabled = true
            signingConfig = signingConfigs.findByName("netliftRelease")
            proguardFiles(
                getDefaultProguardFile("proguard-android-optimize.txt"),
                "proguard-rules.pro",
            )
        }
    }

    lint {
        checkReleaseBuilds = false
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

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
    }
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
}
