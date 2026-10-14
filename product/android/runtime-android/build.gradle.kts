import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("com.android.library")
    id("org.jetbrains.kotlin.android")
    id("maven-publish")
}

group = "io.github.redth.netlift"
version = providers.gradleProperty("netliftVersion").getOrElse("0.1.0-alpha.1")

android {
    namespace = "dev.netlift.runtime"
    compileSdk = 36
    ndkVersion = "28.2.13676358"

    defaultConfig {
        minSdk = 24
        consumerProguardFiles("consumer-rules.pro")
        externalNativeBuild {
            cmake {
                arguments += "-DANDROID_STL=c++_static"
            }
        }
        ndk {
            abiFilters += "arm64-v8a"
        }
        buildConfigField("String", "NETLIFT_VERSION", "\"${project.version}\"")
    }

    buildFeatures {
        buildConfig = true
    }

    externalNativeBuild {
        cmake {
            path = file("src/main/cpp/CMakeLists.txt")
            version = "3.22.1"
        }
    }

    publishing {
        singleVariant("release") {
            withSourcesJar()
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

    dependencies {
        api("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    }
}

publishing {
    publications {
        register<MavenPublication>("release") {
            groupId = project.group.toString()
            artifactId = "runtime-android"
            version = project.version.toString()
            afterEvaluate {
                from(components["release"])
            }
            pom {
                name.set("NetLift Android Runtime")
                description.set("Generic Android runtime for NetLift projected .NET APIs.")
                url.set("https://github.com/Redth/netlift")
                licenses {
                    license {
                        name.set("MIT")
                        url.set("https://opensource.org/license/mit")
                    }
                }
                developers {
                    developer {
                        id.set("redth")
                        name.set("Redth")
                    }
                }
                scm {
                    url.set("https://github.com/Redth/netlift")
                    connection.set("scm:git:https://github.com/Redth/netlift.git")
                }
            }
        }
    }
    repositories {
        maven {
            name = "preview"
            url = uri(rootProject.layout.projectDirectory.dir("../artifacts/maven"))
        }
    }
}
