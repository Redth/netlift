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
            providers.gradleProperty("netliftProjection")
                .orElse("../NetLift.Tests/bin/Debug/net11.0/projection-kotlin-generated-service-facade-v2")
                .get())
        kotlin.srcDir("src/main/kotlin")
    }
}

application {
    mainClass.set("com.netlift.androidhost.contract.KotlinGeneratedServiceFacadeV2SmokeKt")
}
