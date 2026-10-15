import org.jetbrains.kotlin.gradle.dsl.JvmTarget

plugins {
    id("org.jetbrains.kotlin.jvm")
}

java {
    sourceCompatibility = JavaVersion.VERSION_11
    targetCompatibility = JavaVersion.VERSION_11
}

kotlin {
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_11)
    }
}

dependencies {
    // Leitura do QR Code da transmissão pela câmera (veja QrReader).
    implementation("com.google.zxing:core:3.5.3")
    testImplementation(kotlin("test"))
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

tasks.test {
    useJUnitPlatform()
    // O teste contra um Ponte de verdade só roda quando recebe o destino pela variável de ambiente
    // PONTE_TESTE_ALVO (veja InteropTest). Ela entra aqui para o Gradle não reaproveitar um resultado antigo.
    inputs.property("alvoDoTeste", System.getenv("PONTE_TESTE_ALVO") ?: "")
    // Com um destino de verdade, o resultado depende do que está no ar agora: sempre roda de novo.
    if (System.getenv("PONTE_TESTE_ALVO") != null) outputs.upToDateWhen { false }
    testLogging {
        events("passed", "skipped", "failed")
        showStandardStreams = true
    }
}
