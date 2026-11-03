package ponte.core

import java.io.Closeable
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.net.SocketTimeoutException
import java.security.MessageDigest
import java.security.PrivateKey
import java.security.SecureRandom
import java.util.Base64
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference
import javax.net.ssl.SSLContext

/** Resposta de quem está com o aparelho a um pedido de conexão. */
enum class Permission { REFUSE, ONCE, ALWAYS }

/** [id] e [canRemember] só vêm preenchidos quando quem pede provou ter a própria chave, o que permite autorizá-lo de vez. */
class IncomingRequest(val name: String, val user: String, val remote: String, val id: String, val canRemember: Boolean)

/** O que este aparelho guarda para ser acessado: senha, autorizados e o servidor. Implementado pelo aplicativo. */
interface HostConfig {
    val relayServer: String
    val relayKey: String
    /** Nome que aparece para quem acessa. */
    val hostName: String
    val version: String
    /** Sal e verificador da senha em base64 (vazios sem senha) e as iterações do PBKDF2. */
    val passwordSalt: String
    val passwordVerifier: String
    val iterations: Int
    /** Segundos sem uso até a sessão ser encerrada (0 é nunca), quando quem acessa não pede outro tempo. */
    val idleSeconds: Int

    fun isTrusted(keyHash: String): Boolean

    /** Autoriza quem acessa a entrar direto. Falso quando a lista está cheia. */
    fun trust(keyHash: String, id: String, name: String, user: String, viaPassword: Boolean): Boolean

    /** Marca o acesso de agora de um autorizado. Falso se ele saiu da lista nesse meio-tempo. */
    fun touchTrusted(keyHash: String, name: String, user: String): Boolean
}

/** A chave e o certificado deste aparelho como acessado; o ID sai da chave pública. */
class HostKey(val privateKey: PrivateKey, val certificate: ByteArray) {
    val x509 = SelfSignedCertificate.parse(certificate)
    val spki: ByteArray = x509.publicKey.encoded
    val id: String = Crypto.idFromSpki(spki)
    val certificateHash: ByteArray = Crypto.sha256(certificate)
    internal val tlsContext: SSLContext by lazy { EngineTls.serverContext(privateKey, x509) }
}

/** O que o aparelho faz numa sessão: mostrar a tela e receber o que vem de quem acessa. */
interface SessionIo : Closeable {
    /** Largura e altura da imagem agora (mudam ao girar o aparelho). */
    fun size(): Pair<Int, Int>

    /** O próximo quadro (mensagem Frame pronta) ou null quando nada mudou. Pode esperar um pouco pela imagem. */
    fun frame(quality: Int, full: Boolean): ByteArray?

    fun mouse(x: Int, y: Int, action: Int, wheel: Int)
    fun key(virtualKey: Int, up: Boolean)
    fun text(text: String)
    fun clipboard(text: String)

    /** Algo que impede o uso agora (sem controle, sem imagem), para avisar quem acessa; null quando está tudo certo. */
    fun problem(): String?
}

/** O aplicativo: pedidos de conexão, o começo de cada sessão e o estado da conexão com o servidor. */
interface HostPlatform {
    /** Pergunta a quem está com o aparelho. Bloqueia até a resposta (ou recusa sozinho depois de 30 s). */
    fun askPermission(request: IncomingRequest): Permission

    /** Começa a mostrar a tela e a aceitar o controle para uma sessão. */
    fun openSession(session: HostSessionInfo): SessionIo

    fun sessionEnded(session: HostSessionInfo) {}
    fun status(online: Boolean, text: String) {}
}

/** Quem está acessando, para o aplicativo mostrar. */
class HostSessionInfo(val peerName: String, val peerUser: String, val remote: String, val access: String) {
    val started: Long = System.currentTimeMillis()
    internal var stop: (() -> Unit)? = null

    /**
     * Encerra a sessão por aqui. A despedida vai pela rede, então roda numa thread própria: pode ser chamado da
     * thread da tela (o Android derruba o aplicativo que usa a rede nela).
     */
    fun stop() {
        val action = stop ?: return
        PonteHost.thread("ponte-encerrar") { action() }
    }
}

/**
 * Deixa este aparelho acessível pelo ID: mantém o registro no servidor de retransmissão e atende quem chega,
 * do mesmo jeito que o HostService do Windows (protocolo.md, seções 2.2 a 5). Uma sessão por vez.
 */
class PonteHost(private val config: HostConfig, private val key: HostKey, private val platform: HostPlatform) {
    private val running = AtomicBoolean(false)
    private val relaySocket = AtomicReference<Socket?>(null)
    private val current = AtomicReference<HostSessionInfo?>(null)
    private val failures = java.util.concurrent.atomic.AtomicInteger(0)
    @Volatile private var lockedUntil = 0L
    @Volatile var online = false
        private set

    val id: String get() = key.id
    val session: HostSessionInfo? get() = current.get()

    fun start() {
        if (!running.compareAndSet(false, true)) return
        thread("ponte-registro") { relayLoop() }
    }

    fun stop() {
        running.set(false)
        closeQuietly(relaySocket.getAndSet(null))
        current.get()?.stop()
    }

    /** A rede mudou (Wi-Fi, dados móveis): refaz o registro agora em vez de esperar o PING falhar. */
    fun reconnect() {
        closeQuietly(relaySocket.get())
    }

    // ---------- registro no servidor de retransmissão ----------

    private fun relayLoop() {
        while (running.get()) {
            try {
                if (config.relayServer.isBlank()) {
                    setStatus(false, "Sem servidor de retransmissão configurado.")
                    sleep(5000)
                    continue
                }
                val (host, port) = Target.endpoint(config.relayServer, Target.RELAY_PORT)
                val socket = open(host, port)
                relaySocket.set(socket)
                if (!running.get()) { closeQuietly(socket); break }
                socket.soTimeout = 20_000
                val input = socket.getInputStream()
                val output = socket.getOutputStream()
                writeLine(output, relayLine("HOST", key.id))
                var line = readLine(input)
                if (!line.startsWith("NONCE ")) throw PonteException(relayError(line))
                val signature = HostCrypto.sign(key.privateKey, hexToBytes(line.substring(6)))
                val encoder = Base64.getEncoder()
                writeLine(output, "PROOF ${encoder.encodeToString(key.spki)} ${encoder.encodeToString(signature)}")
                line = readLine(input)
                if (line != "OK") throw PonteException(relayError(line))
                setStatus(true, "Online: pronto para ser acessado pelo ID.")

                // O servidor responde PONG; sem resposta por 70 s, a conexão é dada como perdida.
                socket.soTimeout = 0
                val lastPong = AtomicLong(System.nanoTime())
                val pinger = thread("ponte-ping") {
                    try {
                        while (running.get() && !socket.isClosed) {
                            Thread.sleep(25_000)
                            if (System.nanoTime() - lastPong.get() > 70_000_000_000L) { closeQuietly(socket); return@thread }
                            synchronized(output) { writeLine(output, "PING") }
                        }
                    } catch (e: InterruptedException) {
                    } catch (e: IOException) {
                        closeQuietly(socket)
                    }
                }
                try {
                    while (running.get()) {
                        line = readLine(input)
                        when {
                            line == "PONG" -> lastPong.set(System.nanoTime())
                            line.startsWith("INCOMING ") -> {
                                val token = line.substring(9)
                                thread("ponte-pedido") { acceptFromRelay(host, port, token) }
                            }
                        }
                    }
                } finally {
                    pinger.interrupt()
                    closeQuietly(socket)
                }
            } catch (e: PonteException) {
                setStatus(false, e.message ?: "")
            } catch (e: IOException) {
                if (running.get()) setStatus(false, "Sem contato com o servidor de retransmissão; tentando de novo.")
            } catch (e: IllegalArgumentException) {
                setStatus(false, "O servidor de retransmissão respondeu fora do esperado.")
            } finally {
                relaySocket.set(null)
            }
            if (running.get()) sleep(5000)
        }
        setStatus(false, "Desligado.")
    }

    private fun setStatus(online: Boolean, text: String) {
        this.online = online
        platform.status(online, text)
    }

    private fun acceptFromRelay(host: String, port: Int, token: String) {
        val socket = try { open(host, port) } catch (e: PonteException) { return }
        try {
            socket.soTimeout = 20_000
            writeLine(socket.getOutputStream(), relayLine("ACCEPT", token))
            if (readLine(socket.getInputStream()) != "OK") { closeQuietly(socket); return }
        } catch (e: IOException) {
            closeQuietly(socket)
            return
        }
        handle(socket, "pelo servidor de retransmissão")
    }

    // ---------- quem chega ----------

    internal fun handle(socket: Socket, remote: String) {
        var info: HostSessionInfo? = null
        var closeStream: (() -> Unit) = { closeQuietly(socket) }
        try {
            socket.tcpNoDelay = true
            socket.soTimeout = 20_000
            val rawIn = socket.getInputStream()
            val rawOut = socket.getOutputStream().buffered(64 * 1024)
            val first = rawIn.read()
            if (first < 0) return
            val (input, output) = if (first == WebSecure.MAGIC[0].toInt()) {
                val web = WebSecure.accept(rawIn, rawOut, byteArrayOf(first.toByte()), key.privateKey, key.certificate)
                web.input to web.output
            } else {
                val tls = EngineTls.accept(key.tlsContext, rawIn, rawOut, byteArrayOf(first.toByte()))
                closeStream = { tls.close(); closeQuietly(socket) }
                tls.input to tls.output
            }
            val channel = Channel(input, output)

            val hello = channel.expectJson(Msg.HELLO)
            val name = hello.str("Name").take(60)
            val user = hello.str("User").take(60)
            val nonce = ByteArray(32).also { SecureRandom().nextBytes(it) }
            val viewerKey = decodeKey(hello["Key"] as? String)
            val keyHash = viewerKey?.let { HostCrypto.keyHash(it) } ?: ""
            val trusted = viewerKey != null && config.isTrusted(keyHash)
            val hasPassword = config.passwordVerifier.isNotEmpty()
            val passwordOpen = hasPassword && System.currentTimeMillis() >= lockedUntil
            val encoder = Base64.getEncoder()
            channel.sendJson(
                Msg.CHALLENGE,
                mapOf(
                    "HasPassword" to passwordOpen, "Salt" to config.passwordSalt, "Iterations" to config.iterations,
                    "Nonce" to encoder.encodeToString(nonce), "Trusted" to trusted,
                ),
            )
            // Daqui em diante o outro lado pode estar digitando a senha.
            socket.soTimeout = 120_000
            val response = channel.expectJson(Msg.AUTH_RESPONSE)
            val signed = viewerKey != null && HostCrypto.verifyPairing(viewerKey, nonce, key.certificateHash, response["Signature"] as? String)
            val viewerId = if (signed) Crypto.idFromSpki(viewerKey!!) else ""

            var ok = false
            var paired = false
            var reason: String
            var access = ""
            val proof = response["Proof"] as? String
            if (trusted && signed && config.touchTrusted(keyHash, name, user)) {
                ok = true
                paired = true
                reason = ""
                access = "autorização salva"
            } else if (proof != null) {
                if (!passwordOpen) {
                    reason = if (hasPassword) "Muitas tentativas erradas. O acesso por senha está suspenso por alguns minutos."
                    else "Este celular não tem senha de acesso definida."
                } else {
                    val expected = Crypto.proof(Base64.getDecoder().decode(config.passwordVerifier), nonce, key.certificateHash)
                    val given = try { Base64.getDecoder().decode(proof) } catch (e: IllegalArgumentException) { ByteArray(0) }
                    ok = given.size == 32 && MessageDigest.isEqual(expected, given)
                    reason = if (ok) "" else "Senha incorreta."
                    access = "senha"
                    registerAttempt(ok)
                    if (ok && signed && response.bool("Remember")) paired = config.trust(keyHash, viewerId, name, user, viaPassword = true)
                }
            } else {
                socket.soTimeout = 75_000
                val answer = platform.askPermission(IncomingRequest(name, user, remote, viewerId, signed))
                ok = answer != Permission.REFUSE
                reason = if (ok) "" else "O pedido foi recusado."
                access = "aceite na tela"
                if (answer == Permission.ALWAYS && signed) paired = config.trust(keyHash, viewerId, name, user, viaPassword = false)
            }

            var io: SessionIo? = null
            if (ok) {
                info = HostSessionInfo(name, user, remote, access)
                if (!current.compareAndSet(null, info)) {
                    info = null
                    ok = false
                    reason = "Já existe uma sessão em andamento neste celular."
                } else {
                    try {
                        io = platform.openSession(info)
                    } catch (e: PonteException) {
                        current.compareAndSet(info, null)
                        info = null
                        ok = false
                        reason = e.message ?: "O celular não conseguiu começar a sessão."
                    }
                }
            }

            val (width, height) = io?.size() ?: (1 to 1)
            channel.sendJson(
                Msg.AUTH_RESULT,
                mapOf(
                    "Ok" to ok, "Reason" to reason, "HostName" to config.hostName,
                    "Screens" to listOf(mapOf("Index" to 0, "Width" to width, "Height" to height, "Primary" to true)),
                    "Paired" to paired, "Version" to config.version, "Caps" to Caps.PHONE,
                ),
            )
            if (info == null || io == null) return

            channel.limit = Channel.MAX_PAYLOAD
            socket.soTimeout = 0
            val session = HostSession(channel, io, config.idleSeconds)
            session.channelClose = closeStream
            info.stop = { session.stopHere() }
            try {
                session.run()
            } finally {
                io.close()
            }
        } catch (e: IOException) {
        } catch (e: PonteException) {
        } catch (e: IllegalArgumentException) {
        } catch (e: java.security.GeneralSecurityException) {
        } finally {
            info?.let {
                current.compareAndSet(it, null)
                platform.sessionEnded(it)
            }
            closeStream()
        }
    }

    // Cinco senhas erradas seguidas suspendem o acesso por senha por dez minutos.
    private fun registerAttempt(ok: Boolean) {
        if (ok) { failures.set(0); return }
        if (failures.incrementAndGet() < 5) return
        failures.set(0)
        lockedUntil = System.currentTimeMillis() + 10 * 60_000
    }

    private fun decodeKey(text: String?): ByteArray? {
        if (text == null || text.length > 256) return null
        return try { Base64.getDecoder().decode(text).takeIf { it.isNotEmpty() && it.size <= 192 } } catch (e: IllegalArgumentException) { null }
    }

    private fun relayLine(command: String, argument: String): String {
        val relayKey = config.relayKey.filter { it.code < 128 && it.isLetterOrDigit() || it == '-' || it == '_' }
        return if (relayKey.isNotEmpty()) "PONTE $command $argument $relayKey" else "PONTE $command $argument"
    }

    private fun relayError(reply: String): String = when (reply) {
        "ERR key" -> "O servidor de retransmissão recusou a chave de acesso. Confira a chave nas configurações."
        "ERR proof" -> "O servidor de retransmissão não aceitou a identidade deste celular."
        "ERR busy" -> "O servidor de retransmissão está ocupado. Tentando de novo."
        else -> "O servidor de retransmissão recusou o registro ($reply)."
    }

    companion object {
        internal fun open(host: String, port: Int): Socket {
            val socket = Socket()
            try {
                socket.tcpNoDelay = true
                socket.keepAlive = true
                socket.connect(InetSocketAddress(host, port), 10_000)
                return socket
            } catch (e: Exception) {
                closeQuietly(socket)
                if (e is IOException || e is IllegalArgumentException) throw PonteException("Não foi possível alcançar $host:$port.")
                throw e
            }
        }

        internal fun writeLine(output: OutputStream, line: String) {
            output.write((line + "\n").toByteArray(Charsets.US_ASCII))
            output.flush()
        }

        // Lê byte a byte de propósito: nada além da linha pode ser consumido, porque o que vem depois já é TLS.
        internal fun readLine(input: InputStream): String {
            val line = StringBuilder()
            while (line.length < 2048) {
                val b = input.read()
                if (b < 0) throw IOException("O servidor de retransmissão fechou a conexão.")
                if (b == '\n'.code) return line.toString().trimEnd('\r')
                line.append(b.toChar())
            }
            throw IOException("Linha longa demais.")
        }

        internal fun hexToBytes(hex: String): ByteArray {
            require(hex.length % 2 == 0 && hex.length <= 256)
            return ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
        }

        internal fun closeQuietly(closeable: Closeable?) {
            try { closeable?.close() } catch (ignored: IOException) { }
        }

        internal fun thread(name: String, body: () -> Unit): Thread =
            Thread(body, name).apply { isDaemon = true; start() }

        private fun sleep(ms: Long) = try { Thread.sleep(ms) } catch (e: InterruptedException) { }
    }
}

/** Uma sessão em que este aparelho está sendo acessado: envia a tela e aplica o que chega. */
internal class HostSession(private val channel: Channel, private val io: SessionIo, defaultIdle: Int) {
    private val running = AtomicBoolean(true)
    private val forceFull = AtomicBoolean(true)
    @Volatile private var quality = 60
    @Volatile private var viewerIdle = -1
    private val defaultIdle = defaultIdle.coerceIn(0, MAX_IDLE)
    private val lastInput = AtomicLong(System.nanoTime())
    @Volatile private var stoppedHere = false
    private var filesWarned = false

    fun stopHere() {
        stoppedHere = true
        if (running.compareAndSet(true, false)) {
            try { channel.send(Msg.BYE) } catch (ignored: IOException) { }
        }
        // O laço de leitura acorda quando a conexão fecha.
        channelClose?.invoke()
    }

    var channelClose: (() -> Unit)? = null

    private val idleLimit: Int get() = viewerIdle.takeIf { it >= 0 } ?: defaultIdle

    private fun touch() = lastInput.set(System.nanoTime())

    fun run() {
        val capture = PonteHost.thread("ponte-tela") { captureLoop() }
        val watch = PonteHost.thread("ponte-vigia") { watchLoop() }
        try {
            readLoop()
        } catch (e: IOException) {
        } catch (e: PonteException) {
        } finally {
            running.set(false)
            capture.interrupt()
            watch.interrupt()
            try { capture.join(2000) } catch (ignored: InterruptedException) { }
        }
    }

    private fun captureLoop() {
        try {
            while (running.get()) {
                val started = System.nanoTime()
                val frame = io.frame(quality, forceFull.getAndSet(false))
                if (frame != null) channel.send(Msg.FRAME, frame)
                val elapsed = (System.nanoTime() - started) / 1_000_000
                Thread.sleep(maxOf(5L, FRAME_INTERVAL_MS - elapsed))
            }
        } catch (e: InterruptedException) {
        } catch (e: IOException) {
            running.set(false)
        }
    }

    // Tempo sem uso (como no Windows) e avisos do que impede o uso (controle desligado, imagem esperando permissão).
    private fun watchLoop() {
        var warned = false
        var lastProblem: String? = null
        var lastProblemAt = 0L
        try {
            while (running.get()) {
                Thread.sleep(500)
                val limit = idleLimit
                val idle = (System.nanoTime() - lastInput.get()) / 1e9
                if (limit > 0 && idle >= limit) {
                    channel.send(Msg.BYE, ("Sessão encerrada por falta de uso: ${span(limit)} sem mexer no mouse, no teclado ou na tela.").toByteArray(Charsets.UTF_8))
                    running.set(false)
                    channelClose?.invoke()
                    return
                }
                val warning = limit > 0 && idle >= limit - minOf(60.0, limit / 2.0)
                if (warning != warned) {
                    warned = warning
                    channel.send(
                        Msg.NOTICE,
                        (if (warning) "A sessão vai ser encerrada em ${span(maxOf(1, Math.round(limit - idle).toInt()))} por falta de uso. " +
                            "Para continuar, mexa o mouse, use o teclado ou toque na tela."
                        else "Pronto: a sessão continua.").toByteArray(Charsets.UTF_8),
                    )
                }
                val problem = io.problem()
                val now = System.nanoTime()
                if (problem != null && (problem != lastProblem || now - lastProblemAt > 60_000_000_000L)) {
                    channel.send(Msg.NOTICE, problem.toByteArray(Charsets.UTF_8))
                    lastProblemAt = now
                }
                lastProblem = problem
            }
        } catch (e: InterruptedException) {
        } catch (e: IOException) {
        }
    }

    private fun readLoop() {
        while (running.get()) {
            val m = channel.read() ?: return
            val p = m.payload
            when (m.type) {
                Msg.MOUSE -> if (p.size >= 7) {
                    touch()
                    io.mouse(u16(p, 0), u16(p, 2), p[4].toInt() and 0xFF, (u16(p, 5).toShort()).toInt())
                }
                Msg.KEY -> if (p.size >= 3) {
                    touch()
                    io.key(u16(p, 0), (p[2].toInt() and 1) != 0)
                }
                Msg.TEXT -> if (p.size in 1..4096) {
                    touch()
                    io.text(String(p, Charsets.UTF_8))
                }
                Msg.CLIPBOARD -> if (p.size <= 256 * 1024) io.clipboard(String(p, Charsets.UTF_8))
                Msg.FILE_START, Msg.FILE_CHUNK, Msg.FILE_END -> {
                    touch()
                    if (!filesWarned) {
                        filesWarned = true
                        channel.send(Msg.NOTICE, "Este celular ainda não recebe arquivos pelo Ponte.".toByteArray(Charsets.UTF_8))
                    }
                }
                Msg.SETTINGS -> {
                    val s = try { Json.parseObject(String(p, Charsets.UTF_8)) } catch (e: IllegalArgumentException) { emptyMap() }
                    val wanted = (s["IdleSeconds"] as? Number)?.toInt()
                    viewerIdle = wanted?.coerceIn(0, MAX_IDLE) ?: -1
                    val q = s.int("Quality", quality).coerceIn(10, 95)
                    if (q != quality) forceFull.set(true)
                    quality = q
                }
                Msg.BYE -> return
            }
        }
    }

    private fun u16(p: ByteArray, at: Int) = (p[at].toInt() and 0xFF) or ((p[at + 1].toInt() and 0xFF) shl 8)

    companion object {
        /** No máximo uns 20 quadros por segundo: o celular gasta bateria e dados para cada um. */
        const val FRAME_INTERVAL_MS = 50L
        private const val MAX_IDLE = 7 * 24 * 3600

        /** "40 segundos", "1 minuto", "10 minutos", "1 hora", "1 h 30 min" (como no Windows). */
        fun span(seconds: Int): String {
            if (seconds < 60) return if (seconds == 1) "1 segundo" else "$seconds segundos"
            val minutes = Math.round(seconds / 60.0).toInt()
            if (minutes < 60) return if (minutes == 1) "1 minuto" else "$minutes minutos"
            val hours = minutes / 60
            val rest = minutes % 60
            return if (rest > 0) "$hours h $rest min" else if (hours == 1) "1 hora" else "$hours horas"
        }
    }
}
