import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("io.github.redth.netlift") version "0.1.0-alpha.4"
}

netlift {
    managedProject.set(
        rootProject.file(
            "../../../../product/samples/Orders.Contracts/Orders.Contracts.csproj"
        )
    )
    componentProject.set(
        rootProject.file(
            "../../android/managed-component/NetLift.WorkloadLibraryMode.Android.csproj"
        )
    )
    contractRoots.set(listOf("Orders.Contracts.IOrdersService"))
    kotlinPackage.set("dev.netlift.orders")
    swiftModule.set("OrdersNetLift")
    componentApplicationId.set("dev.netlift.workloadlibrarymode.component")
    compatibilityLock.set(
        rootProject.file("../../../../product/contracts/orders.netlift.lock.json")
    )
}

android {
    namespace = "dev.netlift.workloadlibrarymode.app"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        applicationId = "dev.netlift.workloadlibrarymode.app"
        minSdk = 24
        targetSdk = 36
        versionCode = 1
        versionName = "0.1"
        ndk {
            abiFilters += "arm64-v8a"
        }

    }

    buildTypes {
        release {
            isMinifyEnabled = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"))
        }
    }

    lint {
        checkReleaseBuilds = false
    }

    packaging {
        jniLibs {
            keepDebugSymbols += "**/*.so"
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
}
