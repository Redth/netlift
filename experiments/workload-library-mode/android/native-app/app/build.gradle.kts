import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
}

val workloadComponent = rootProject.file(
    "../../.artifacts/android-component/netlift-workload-component-debug.aar"
)
val productRoot = rootProject.file("../../../../product")
val generatedContracts = productRoot.resolve("artifacts/generated")

val buildWorkloadComponent = tasks.register<Exec>("buildWorkloadComponent") {
    workingDir(rootProject.file("../.."))
    commandLine("python3", "android/build_component.py")
    inputs.files(
        rootProject.fileTree("../../android/managed-component") {
            exclude("bin/**", "obj/**")
        },
        rootProject.file("../../android/build_component.py"),
        rootProject.file("../../shared/inventory_workloads.py"),
        rootProject.file("../../proof.json"),
        rootProject.fileTree(productRoot.resolve("src")) {
            exclude("**/bin/**", "**/obj/**")
        },
        rootProject.fileTree(productRoot.resolve("samples")) {
            exclude("**/bin/**", "**/obj/**")
        },
        productRoot.resolve("netlift.json"),
    )
    outputs.file(workloadComponent)
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

        sourceSets {
            named("main") {
                java.srcDir(generatedContracts)
            }
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
    implementation("io.github.redth.netlift:runtime-android:0.1.0-alpha.1")
    implementation(files(workloadComponent).builtBy(buildWorkloadComponent))
}

tasks.configureEach {
    if (name.startsWith("compile") && name.endsWith("Kotlin")) {
        dependsOn(buildWorkloadComponent)
    }
}
