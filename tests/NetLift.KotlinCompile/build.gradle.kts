plugins {
    kotlin("jvm") version "2.2.20"
    application
}

dependencies {
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-core:1.10.2")
}

repositories {
    mavenCentral()
}

kotlin {
    jvmToolchain(21)
    sourceSets.main {
        kotlin.srcDir("../NetLift.Tests/bin/Debug/net11.0/projection-edge-cases")
        kotlin.srcDir("../NetLift.NativeHost.Managed/obj/projected")
        kotlin.srcDir("src/main/kotlin")
        System.getenv("NETLIFT_STREAM_PROJECTION")
            ?.takeIf { it.isNotBlank() }
            ?.let { kotlin.srcDir(it) }
    }
}

application {
    mainClass.set("com.netlift.androidhost.contract.GeneratedOrdersSmokeKt")
}
