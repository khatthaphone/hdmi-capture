// Pure Kotlin (JVM): format choice, capture state + recovery, signal analysis, presets.
// No Android dependencies, so it tests fast and mirrors CaptureCore's platform-free code.
plugins {
    alias(libs.plugins.kotlin.jvm)
    alias(libs.plugins.kotlin.serialization)
}

kotlin {
    jvmToolchain(21)
}

dependencies {
    api(libs.kotlinx.serialization.json)
    testImplementation(libs.junit)
}

// Shared JSON vectors, also read by the Swift tests (docs/fixtures/README.md).
val fixtures = rootDir.resolve("../../docs/fixtures").canonicalPath
tasks.test {
    systemProperty("fixtures", fixtures)
    inputs.dir(fixtures)
}
