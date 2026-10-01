plugins {
    kotlin("jvm") version "2.2.20"
    application
}

repositories {
    mavenCentral()
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
}

kotlin {
    jvmToolchain(21)
    sourceSets.main {
        kotlin.srcDir(
            System.getenv("NETLIFT_TYPED_REPLY_PROJECTION")
                ?.takeIf { it.isNotBlank() }
                ?: "../NetLift.Tests/bin/Debug/net11.0/projection-typed-reply-lifecycle-v2")
        kotlin.srcDir(
            System.getenv("NETLIFT_FLAT_CODEC_PROJECTION")
                ?.takeIf { it.isNotBlank() }
                ?: "../NetLift.Tests/bin/Debug/net11.0/projection-flat-codec-tests")
        kotlin.srcDir("src/main/kotlin")
    }
}

application {
    mainClass.set("com.netlift.androidhost.contract.TypedReplySmokeKt")
}
