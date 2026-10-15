package ponte.core

import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.Base64
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLException
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

/** O que identifica este aparelho para o outro lado e como chegar ao servidor de retransmissão. */
class ClientConfig(
    val relayServer: String,
    val relayKey: String,
    val deviceName: String,
    val userName: String,
    val version: String,
)

interface ConnectCallbacks {
    /** Chamado quando o outro lado pede senha. Devolve a senha, "" para pedir permissão lá, ou null para desistir. */
    fun askPassword(): String?

    /** Chamado quando a identidade do destino mudou desde a última conexão. */
    fun confirmNewIdentity(): Boolean
}

class ScreenInfo(val index: Int, val width: Int, val height: Int, val primary: Boolean)

/** Um retângulo de um quadro: a posição na tela e o JPEG dele, dentro de [data]. */
class Tile(val x: Int, val y: Int, val width: Int, val height: Int, val data: ByteArray, val offset: Int, val length: Int)

/** Um quadro traz só as áreas da tela que mudaram. */
class Frame(val width: Int, val height: Int, val tiles: List<Tile>)

interface SessionListener {
    fun onFrame(frame: Frame)
    fun onClipboard(text: String)
    fun onNotice(text: String)

    /** O ponteiro do mouse de lá mudou de forma; null quando ele está escondido. Só chega de um Ponte 1.5.0 ou mais novo. */
    fun onPointer(shape: PointerShape?) {}

    /**
     * O ponteiro de lá mudou de lugar: [x] e [y] de 0 a 65535 na tela mostrada, na escala da mensagem de
     * mouse; [onScreen] falso quando ele está em outro monitor. Só chega de um Ponte 1.5.1 ou mais novo.
     */
    fun onPointerPosition(x: Int, y: Int, onScreen: Boolean) {}

    /** Um trecho do som de lá. Só chega depois de pedido em [Session.sendSettings]. */
    fun onSound(sound: Sound) {}

    /** Fim da sessão. O motivo vem vazio quando foi este lado que encerrou. */
    fun onClosed(reason: String)
}

object PonteClient {
    private const val CONNECT_TIMEOUT_MS = 10_000

    /**
     * Conecta e autentica; devolve a sessão pronta. Bloqueia: chame fora da thread da interface.
     *
     * @param knownFingerprint identidade vista da última vez neste destino, ou null na primeira conexão
     * @param remember pede ao outro lado para este aparelho ficar autorizado ao entrar com a senha
     */
    @Throws(PonteException::class)
    fun connect(
        config: ClientConfig,
        targetText: String,
        identity: Identity,
        knownFingerprint: String?,
        remember: Boolean,
        callbacks: ConnectCallbacks,
    ): Session {
        val target = Target.parse(targetText)
        val socket = if (target.isId) openThroughRelay(config, target.id) else open(target.host, target.port)
        try {
            val trust = RemoteIdentityCheck(if (target.isId) target.id else null)
            val context = SSLContext.getInstance("TLS")
            context.init(null, arrayOf(trust), null)
            val ssl = context.socketFactory.createSocket(socket, "ponte", socket.port, true) as SSLSocket
            ssl.useClientMode = true
            ssl.soTimeout = 20_000
            try {
                ssl.startHandshake()
            } catch (e: SSLException) {
                if (trust.wrongId) throw PonteException("A identidade do computador remoto não confere com o ID digitado. A conexão foi recusada.")
                throw PonteException("Falha ao estabelecer a conexão segura.")
            }
            val certificate = trust.certificate ?: throw PonteException("O outro computador não apresentou identidade.")
            val certificateHash = Crypto.sha256(certificate.encoded)
            val fingerprint = Crypto.hex(certificateHash)
            if (knownFingerprint != null && knownFingerprint != fingerprint && !callbacks.confirmNewIdentity())
                throw PonteException("Conexão cancelada.")

            val channel = Channel(ssl.inputStream, ssl.outputStream.buffered())
            val encoder = Base64.getEncoder()
            channel.sendJson(
                Msg.HELLO,
                mapOf(
                    "Name" to config.deviceName, "User" to config.userName, "Version" to config.version, "Key" to encoder.encodeToString(identity.spki),
                    "Caps" to (Caps.CURSOR or Caps.AUDIO),
                ),
            )
            val challenge = channel.expectJson(Msg.CHALLENGE)
            val nonce = decode(challenge.str("Nonce"))
            if (nonce.size != 32) throw PonteException("O outro computador enviou um desafio inválido.")
            // A assinatura vai sempre: é ela que deixa o outro lado reconhecer este aparelho, agora ou numa próxima vez.
            val signature = encoder.encodeToString(identity.sign(Crypto.pairingMessage(nonce, certificateHash)))

            var proof: String? = null
            if (challenge.bool("HasPassword") && !challenge.bool("Trusted")) {
                ssl.soTimeout = 0
                val password = callbacks.askPassword() ?: throw PonteException("Conexão cancelada.")
                if (password.isNotEmpty()) {
                    val iterations = challenge.int("Iterations")
                    if (iterations !in 1000..2_000_000) throw PonteException("O outro computador enviou parâmetros de senha inválidos.")
                    val verifier = Crypto.pbkdf2(password, decode(challenge.str("Salt")), iterations)
                    proof = encoder.encodeToString(Crypto.proof(verifier, nonce, certificateHash))
                }
            }
            channel.sendJson(Msg.AUTH_RESPONSE, mapOf("Proof" to proof, "Signature" to signature, "Remember" to remember))

            // Sem senha nem autorização, a resposta depende de alguém aceitar o pedido do outro lado.
            ssl.soTimeout = 75_000
            val result = channel.expectJson(Msg.AUTH_RESULT)
            if (!result.bool("Ok")) throw PonteException(result.str("Reason").ifEmpty { "Conexão recusada." })

            channel.limit = Channel.MAX_PAYLOAD
            ssl.soTimeout = 0
            val screens = result.objects("Screens").map { ScreenInfo(it.int("Index"), it.int("Width"), it.int("Height"), it.bool("Primary")) }
            return Session(ssl, channel, target, result.str("HostName"), screens, result.bool("Paired"), result.str("Version").take(20), fingerprint, result.int("Caps"))
        } catch (e: Exception) {
            try { socket.close() } catch (ignored: IOException) { }
            throw when (e) {
                is PonteException -> e
                is SocketTimeoutException -> PonteException("O outro computador não respondeu a tempo.")
                is IOException, is IllegalArgumentException -> PonteException("A conexão caiu durante a identificação.")
                else -> e
            }
        }
    }

    private fun open(host: String, port: Int): Socket {
        val socket = Socket()
        try {
            socket.tcpNoDelay = true
            socket.keepAlive = true
            socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
            return socket
        } catch (e: Exception) {
            try { socket.close() } catch (ignored: IOException) { }
            if (e is IOException || e is IllegalArgumentException) throw PonteException("Não foi possível alcançar $host:$port.")
            throw e
        }
    }

    private fun openThroughRelay(config: ClientConfig, id: String): Socket {
        if (config.relayServer.isBlank())
            throw PonteException("Para conectar por ID é preciso configurar o servidor de retransmissão. Na mesma rede, use o endereço IP.")
        val (host, port) = Target.endpoint(config.relayServer, Target.RELAY_PORT)
        val socket = open(host, port)
        try {
            val key = config.relayKey.filter { it.isLetterOrDigit() && it.code < 128 || it == '-' || it == '_' }
            val line = if (key.isNotEmpty()) "PONTE CONNECT $id $key\n" else "PONTE CONNECT $id\n"
            socket.getOutputStream().apply {
                write(line.toByteArray(Charsets.US_ASCII))
                flush()
            }
            // O servidor espera até 20 s o outro computador atender.
            socket.soTimeout = 30_000
            val reply = readLine(socket.getInputStream())
            if (reply != "OK") throw PonteException(relayError(reply))
            return socket
        } catch (e: Exception) {
            try { socket.close() } catch (ignored: IOException) { }
            throw when (e) {
                is PonteException -> e
                is SocketTimeoutException -> PonteException("O outro computador não respondeu a tempo.")
                is IOException -> PonteException("O servidor de retransmissão fechou a conexão.")
                else -> e
            }
        }
    }

    // Lê byte a byte de propósito: nada além da linha pode ser consumido, porque o que vem depois já é TLS.
    private fun readLine(input: InputStream): String {
        val line = StringBuilder()
        while (line.length < 1024) {
            val b = input.read()
            if (b < 0) throw IOException("fim da conexão")
            if (b == '\n'.code) return line.toString().trimEnd('\r')
            line.append(b.toChar())
        }
        throw IOException("linha longa demais")
    }

    private fun relayError(reply: String): String = when (reply) {
        "ERR key" -> "O servidor de retransmissão recusou a chave de acesso. Confira a chave nas configurações."
        "ERR offline" -> "Esse computador não está online."
        "ERR timeout" -> "O outro computador não respondeu a tempo."
        "ERR busy" -> "O servidor de retransmissão está ocupado. Tente de novo."
        else -> "O servidor de retransmissão recusou a conexão ($reply)."
    }

    private fun decode(text: String): ByteArray =
        try { Base64.getDecoder().decode(text) } catch (e: IllegalArgumentException) { throw PonteException("O outro computador respondeu fora do esperado.") }

    /**
     * Não há autoridade certificadora: cada computador tem um certificado próprio. Numa conexão por ID,
     * o ID calculado da chave apresentada tem de ser o digitado, senão o aperto de mão já falha aqui.
     * A comparação com a identidade vista da última vez é feita logo depois, porque pode pedir uma
     * decisão ao usuário.
     */
    private class RemoteIdentityCheck(private val expectedId: String?) : X509TrustManager {
        var certificate: X509Certificate? = null
        var wrongId = false

        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            val first = chain?.firstOrNull() ?: throw CertificateException("sem certificado")
            if (expectedId != null && Crypto.idFromSpki(first.publicKey.encoded) != expectedId) {
                wrongId = true
                throw CertificateException("o ID não confere com a chave apresentada")
            }
            certificate = first
        }

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) {
            throw CertificateException("este lado nunca atua como servidor")
        }

        override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
    }
}

/** Uma sessão em que este aparelho controla um computador. */
class Session internal constructor(
    private val socket: SSLSocket,
    private val channel: Channel,
    val target: Target,
    val hostName: String,
    val screens: List<ScreenInfo>,
    /** Este aparelho está na lista de autorizados do outro lado: as próximas conexões entram direto. */
    val paired: Boolean,
    /** Versão do Ponte no computador; vazia quando ele é anterior à 1.2.1. */
    val hostVersion: String,
    val fingerprint: String,
    hostCaps: Int = 0,
) : Closeable {
    // Um Android não deixa usar a rede na thread da interface; os envios passam por esta fila, em ordem.
    private val sender: ExecutorService = Executors.newSingleThreadExecutor { r -> Thread(r, "ponte-envio").apply { isDaemon = true } }

    @Volatile
    private var closedLocally = false

    /** O computador sabe receber texto pronto (1.3.0 em diante); antes disso só teclas. */
    val acceptsText: Boolean = versionAtLeast(hostVersion, 1, 3)

    /** O computador transmite o som dele quando pedido (1.5.0 em diante, e se o dono não desligou). */
    val hasSound: Boolean = (hostCaps and Caps.AUDIO) != 0

    /** Lê as mensagens até a sessão acabar. Bloqueia: rode numa thread própria. */
    fun run(listener: SessionListener) {
        var reason = "O outro computador encerrou a sessão."
        try {
            while (true) {
                val message = channel.read() ?: break
                when (message.type) {
                    Msg.FRAME -> listener.onFrame(parseFrame(message.payload))
                    Msg.CURSOR -> if (message.payload.size in 5..MAX_POINTER) listener.onPointer(parsePointer(message.payload))
                    Msg.CURSOR_POS -> parsePointerPosition(message.payload)?.let { listener.onPointerPosition(it[0], it[1], it[2] == 1) }
                    Msg.AUDIO -> AudioCodec.decode(message.payload)?.let { listener.onSound(it) }
                    Msg.CLIPBOARD -> if (message.payload.size <= 256 * 1024) listener.onClipboard(String(message.payload, Charsets.UTF_8))
                    Msg.NOTICE -> if (message.payload.size <= 2048) listener.onNotice(String(message.payload, Charsets.UTF_8))
                    Msg.BYE -> break
                }
            }
        } catch (e: Exception) {
            if (e !is IOException && e !is PonteException) throw e
            reason = if (e is PonteException) e.message ?: "" else "A conexão com o outro computador caiu."
        } finally {
            val local = closedLocally
            close()
            listener.onClosed(if (local) "" else reason)
        }
    }

    fun sendMouse(x: Int, y: Int, action: Int, wheel: Int = 0) {
        val nx = x.coerceIn(0, 65535)
        val ny = y.coerceIn(0, 65535)
        val w = wheel.coerceIn(Short.MIN_VALUE.toInt(), Short.MAX_VALUE.toInt())
        enqueue(Msg.MOUSE, byteArrayOf(nx.toByte(), (nx shr 8).toByte(), ny.toByte(), (ny shr 8).toByte(), action.toByte(), w.toByte(), (w shr 8).toByte()))
    }

    /** @param virtualKey código de tecla virtual do Windows */
    fun sendKey(virtualKey: Int, up: Boolean, extended: Boolean = false) {
        val flags = (if (up) 1 else 0) or (if (extended) 2 else 0)
        enqueue(Msg.KEY, byteArrayOf(virtualKey.toByte(), (virtualKey shr 8).toByte(), flags.toByte()))
    }

    /** Texto pronto para o computador digitar, em pedaços que cabem numa mensagem. */
    fun sendText(text: String) {
        var start = 0
        while (start < text.length) {
            var end = minOf(text.length, start + 500)
            // Não separa um par substituto (emoji) em duas mensagens.
            if (end < text.length && Character.isHighSurrogate(text[end - 1])) end--
            enqueue(Msg.TEXT, text.substring(start, end).toByteArray(Charsets.UTF_8))
            start = end
        }
    }

    /** @param sound pede o som do computador; um Ponte anterior à 1.5.0 ignora o pedido */
    fun sendSettings(screen: Int, quality: Int, sound: Boolean = false) =
        enqueue(Msg.SETTINGS, Json.write(mapOf("Screen" to screen, "Quality" to quality, "Audio" to sound)).toByteArray(Charsets.UTF_8))

    fun sendClipboard(text: String) = enqueue(Msg.CLIPBOARD, text.toByteArray(Charsets.UTF_8))

    private fun enqueue(type: Int, payload: ByteArray) {
        try {
            sender.execute {
                // Se a conexão caiu, quem avisa é o laço de leitura.
                try { channel.send(type, payload) } catch (ignored: IOException) { }
            }
        } catch (ignored: RejectedExecutionException) {
        }
    }

    override fun close() {
        if (closedLocally) return
        closedLocally = true
        try {
            sender.execute {
                try { channel.send(Msg.BYE) } catch (ignored: IOException) { }
                try { socket.close() } catch (ignored: IOException) { }
            }
            sender.shutdown()
        } catch (ignored: RejectedExecutionException) {
            try { socket.close() } catch (ignored2: IOException) { }
        }
    }

    companion object {
        private const val MAX_POINTER = 512 * 1024

        /** Formato: 1 byte (1 = visível), o ponto ativo x e y (2 bytes cada) e a imagem em PNG. */
        internal fun parsePointer(p: ByteArray): PointerShape? {
            if (p.size <= 5 || (p[0].toInt() and 1) == 0) return null
            fun u16(at: Int) = (p[at].toInt() and 0xFF) or ((p[at + 1].toInt() and 0xFF) shl 8)
            return PointerShape(u16(1), u16(3), p, 5, p.size - 5)
        }

        /** Formato: 1 byte (1 = na tela mostrada), x e y (2 bytes cada). Devolve x, y e a marca. */
        internal fun parsePointerPosition(p: ByteArray): IntArray? {
            if (p.size < 5) return null
            fun u16(at: Int) = (p[at].toInt() and 0xFF) or ((p[at + 1].toInt() and 0xFF) shl 8)
            return intArrayOf(u16(1), u16(3), p[0].toInt() and 1)
        }

        internal fun versionAtLeast(version: String, major: Int, minor: Int): Boolean {
            val parts = version.split('.').map { it.toIntOrNull() ?: return false }
            if (parts.size < 2) return false
            return parts[0] > major || (parts[0] == major && parts[1] >= minor)
        }

        /** Formato: largura, altura e quantidade (2 bytes cada); depois, por retângulo, x, y, w, h (2 bytes cada), tamanho (4) e o JPEG. */
        internal fun parseFrame(p: ByteArray): Frame {
            fun u16(at: Int) = (p[at].toInt() and 0xFF) or ((p[at + 1].toInt() and 0xFF) shl 8)
            if (p.size < 6) throw PonteException("O outro computador enviou uma imagem incompleta.")
            val width = u16(0)
            val height = u16(2)
            val count = u16(4)
            if (width == 0 || height == 0) throw PonteException("O outro computador enviou uma imagem sem dimensões.")
            val tiles = ArrayList<Tile>(count)
            var offset = 6
            repeat(count) {
                if (offset + 12 > p.size) throw PonteException("O outro computador enviou uma imagem incompleta.")
                val x = u16(offset)
                val y = u16(offset + 2)
                val w = u16(offset + 4)
                val h = u16(offset + 6)
                val length = u16(offset + 8) or (u16(offset + 10) shl 16)
                offset += 12
                if (length <= 0 || length > p.size - offset || x + w > width || y + h > height)
                    throw PonteException("O outro computador enviou uma imagem incompleta.")
                tiles.add(Tile(x, y, w, h, p, offset, length))
                offset += length
            }
            return Frame(width, height, tiles)
        }
    }
}
