package ponte.core

import java.awt.Color
import java.awt.Font
import java.awt.image.BufferedImage
import java.io.ByteArrayOutputStream
import java.io.File
import java.security.KeyPairGenerator
import java.security.spec.ECGenParameterSpec
import java.util.Base64
import javax.imageio.IIOImage
import javax.imageio.ImageIO
import javax.imageio.ImageWriteParam
import kotlin.test.Test

/**
 * Um "celular" de mentira no servidor de verdade, para testar o acesso a um celular com os visualizadores reais
 * (Ponte do Windows, aplicativo e navegador). Só roda com PONTE_TESTE_ACESSO_SEGUNDOS (quanto tempo fica no ar),
 * PONTE_TESTE_RELAY, PONTE_TESTE_CHAVE e PONTE_TESTE_SAIDA (arquivo onde vão o ID e o que chegou de quem acessa).
 * A senha de acesso é PONTE_TESTE_SENHA.
 */
class AcessoAoVivoTest {
    @Test
    fun celularDeMentiraNoServidor() {
        val seconds = System.getenv("PONTE_TESTE_ACESSO_SEGUNDOS")?.toIntOrNull() ?: return
        val out = File(System.getenv("PONTE_TESTE_SAIDA") ?: "acesso-ao-vivo.txt")
        val password = System.getenv("PONTE_TESTE_SENHA") ?: "teste-acesso"
        fun log(line: String) = synchronized(out) { out.appendText(line + "\n") }
        out.writeText("")

        val salt = ByteArray(16) { 1 }
        val config = object : HostConfig {
            override val relayServer = System.getenv("PONTE_TESTE_RELAY") ?: ""
            override val relayKey = System.getenv("PONTE_TESTE_CHAVE") ?: ""
            override val hostName = "Celular de teste"
            override val version = "1.14.0"
            override val passwordSalt = Base64.getEncoder().encodeToString(salt)
            override val passwordVerifier = Base64.getEncoder().encodeToString(HostCrypto.verifier(password, salt, 1000))
            override val iterations = 1000
            override val idleSeconds = 0
            override fun isTrusted(keyHash: String) = false
            override fun trust(keyHash: String, id: String, name: String, user: String, viaPassword: Boolean) = false
            override fun touchTrusted(keyHash: String, name: String, user: String) = false
        }
        val pair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
        val key = HostKey(pair.private, SelfSignedCertificate.create(pair))
        val width = 540
        val height = 1200
        val platform = object : HostPlatform {
            override fun askPermission(request: IncomingRequest): Permission {
                log("pedido de ${request.name} (${request.user})")
                return Permission.ONCE
            }

            override fun status(online: Boolean, text: String) = log("estado: $online $text")

            override fun openSession(session: HostSessionInfo): SessionIo {
                log("sessão de ${session.peerName} (${session.access})")
                val builder = FrameBuilder()
                val image = BufferedImage(width, height, BufferedImage.TYPE_INT_RGB)
                var tick = 0
                var touch: Pair<Int, Int>? = null
                var typed = ""
                return object : SessionIo {
                    override fun size() = width to height
                    override fun frame(quality: Int, full: Boolean): ByteArray? {
                        tick++
                        val g = image.createGraphics()
                        g.color = Color(0x00, 0x69, 0x78)
                        g.fillRect(0, 0, width, height)
                        g.color = Color.WHITE
                        g.font = Font(Font.SANS_SERIF, Font.BOLD, 40)
                        g.drawString("Celular de teste", 60, 120)
                        g.font = Font(Font.SANS_SERIF, Font.PLAIN, 30)
                        g.drawString("quadro ${tick / 10}", 60, 200)
                        g.drawString("texto: $typed", 60, 280)
                        g.color = Color.YELLOW
                        g.fillRect((tick * 6) % (width - 80), 400, 80, 80)
                        touch?.let { (x, y) -> g.color = Color.RED; g.fillOval(x * width / 65536 - 20, y * height / 65536 - 20, 40, 40) }
                        g.dispose()
                        val pixels = image.getRGB(0, 0, width, height, null, 0, width)
                        return builder.build(pixels, width, height, quality, full) { x, y, w, h, q ->
                            val writer = ImageIO.getImageWritersByFormatName("jpeg").next()
                            val bytes = ByteArrayOutputStream()
                            ImageIO.createImageOutputStream(bytes).use { stream ->
                                writer.output = stream
                                val param = writer.defaultWriteParam.apply { compressionMode = ImageWriteParam.MODE_EXPLICIT; compressionQuality = q / 100f }
                                writer.write(null, IIOImage(image.getSubimage(x, y, w, h), null, null), param)
                            }
                            writer.dispose()
                            bytes.toByteArray()
                        }
                    }
                    override fun mouse(x: Int, y: Int, action: Int, wheel: Int) {
                        if (action != MouseAction.MOVE) log("mouse $x $y $action $wheel")
                        if (action == MouseAction.LEFT_DOWN) touch = x to y
                    }
                    override fun key(virtualKey: Int, up: Boolean) {
                        log("tecla $virtualKey ${if (up) "solta" else "aperta"}")
                    }
                    override fun text(text: String) {
                        log("texto $text")
                        typed = (typed + text).takeLast(20)
                    }
                    override fun clipboard(text: String) = log("copiado $text")
                    override fun problem(): String? = null
                    override fun close() = log("fim da sessão")
                }
            }
        }
        val host = PonteHost(config, key, platform)
        log("id ${key.id}")
        host.start()
        Thread.sleep(seconds * 1000L)
        host.stop()
    }
}
