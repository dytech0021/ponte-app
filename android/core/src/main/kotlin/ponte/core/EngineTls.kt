package ponte.core

import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.security.KeyStore
import java.security.PrivateKey
import java.security.cert.X509Certificate
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLEngineResult
import javax.net.ssl.SSLException

/**
 * TLS do lado de quem é acessado, feito com SSLEngine sobre os fluxos da conexão. Assim o primeiro byte, já
 * lido para saber se quem chegou é o Ponte no navegador ("W") ou um TLS (0x16), volta para o começo do fluxo,
 * e o mesmo código roda no Android e nos testes no computador.
 */
class EngineTls private constructor(
    private val engine: SSLEngine,
    private val rawIn: InputStream,
    private val rawOut: OutputStream,
) {
    private val readLock = Any()
    private val writeLock = Any()
    private var netIn: ByteBuffer = ByteBuffer.allocate(engine.session.packetBufferSize).apply { flip() }
    private var appIn: ByteBuffer = ByteBuffer.allocate(engine.session.applicationBufferSize).apply { flip() }
    private var netOut: ByteBuffer = ByteBuffer.allocate(engine.session.packetBufferSize)
    private var closed = false

    val input: InputStream = object : InputStream() {
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) <= 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            synchronized(readLock) {
                while (!appIn.hasRemaining()) {
                    if (!unwrapOnce()) return -1
                }
                val n = minOf(len, appIn.remaining())
                appIn.get(b, off, n)
                return n
            }
        }
    }

    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

        override fun write(b: ByteArray, off: Int, len: Int) {
            synchronized(writeLock) {
                if (closed) throw IOException("Conexão encerrada.")
                val source = ByteBuffer.wrap(b, off, len)
                while (source.hasRemaining()) wrap(source)
            }
        }

        override fun flush() = synchronized(writeLock) { rawOut.flush() }

        override fun close() = this@EngineTls.close()
    }

    /** Avisa o fim ao outro lado (quando dá) e fecha a conexão. */
    fun close() {
        synchronized(writeLock) {
            if (closed) return
            closed = true
            try {
                engine.closeOutbound()
                while (!engine.isOutboundDone) wrap(ByteBuffer.allocate(0))
                rawOut.flush()
            } catch (ignored: Exception) {
            }
        }
        try { rawIn.close() } catch (ignored: IOException) { }
        try { rawOut.close() } catch (ignored: IOException) { }
    }

    // Cifra e manda um pedaço (ou uma mensagem de controle do TLS, com a fonte vazia).
    private fun wrap(source: ByteBuffer) {
        while (true) {
            netOut.clear()
            val result = engine.wrap(source, netOut)
            when (result.status) {
                SSLEngineResult.Status.BUFFER_OVERFLOW -> {
                    netOut = ByteBuffer.allocate(maxOf(netOut.capacity() * 2, engine.session.packetBufferSize))
                    continue
                }
                SSLEngineResult.Status.CLOSED -> {
                    flushNet()
                    if (source.hasRemaining()) throw IOException("Conexão encerrada.")
                    return
                }
                else -> {
                    flushNet()
                    runTasks(result)
                    return
                }
            }
        }
    }

    private fun flushNet() {
        netOut.flip()
        if (netOut.hasRemaining()) rawOut.write(netOut.array(), netOut.position(), netOut.remaining())
    }

    // Decifra o próximo registro. Falso no fim da conexão.
    private fun unwrapOnce(): Boolean {
        while (true) {
            appIn.compact()
            val result = try {
                engine.unwrap(netIn, appIn)
            } finally {
                appIn.flip()
            }
            when (result.status) {
                SSLEngineResult.Status.OK -> {
                    runTasks(result)
                    // Depois do aperto de mão, o TLS 1.3 pode pedir uma resposta (troca de chaves, por exemplo).
                    if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.NEED_WRAP) {
                        synchronized(writeLock) { if (!closed) wrap(ByteBuffer.allocate(0)); rawOut.flush() }
                    }
                    if (appIn.hasRemaining() || result.bytesConsumed() > 0) return true
                }
                SSLEngineResult.Status.BUFFER_UNDERFLOW -> if (!fillNet()) return false
                SSLEngineResult.Status.BUFFER_OVERFLOW -> {
                    val bigger = ByteBuffer.allocate(appIn.remaining() + engine.session.applicationBufferSize)
                    bigger.put(appIn)
                    bigger.flip()
                    appIn = bigger
                }
                SSLEngineResult.Status.CLOSED -> return false
                null -> return false
            }
        }
    }

    // Lê mais bytes da rede para o buffer de entrada. Falso no fim da conexão.
    private fun fillNet(): Boolean {
        if (netIn.limit() == netIn.capacity() && netIn.position() == 0) {
            val bigger = ByteBuffer.allocate(netIn.capacity() * 2)
            bigger.put(netIn)
            bigger.flip()
            netIn = bigger
        }
        netIn.compact()
        try {
            val n = rawIn.read(netIn.array(), netIn.arrayOffset() + netIn.position(), netIn.remaining())
            if (n < 0) return false
            netIn.position(netIn.position() + n)
            return true
        } finally {
            netIn.flip()
        }
    }

    private fun runTasks(result: SSLEngineResult) {
        if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.NEED_TASK) {
            while (true) engine.delegatedTask?.run() ?: break
        }
    }

    // O aperto de mão inteiro, antes de qualquer mensagem do Ponte.
    private fun handshake() {
        engine.beginHandshake()
        var status = engine.handshakeStatus
        while (status != SSLEngineResult.HandshakeStatus.FINISHED && status != SSLEngineResult.HandshakeStatus.NOT_HANDSHAKING) {
            status = when (status) {
                SSLEngineResult.HandshakeStatus.NEED_WRAP -> {
                    netOut.clear()
                    val result = engine.wrap(ByteBuffer.allocate(0), netOut)
                    if (result.status == SSLEngineResult.Status.BUFFER_OVERFLOW) {
                        netOut = ByteBuffer.allocate(netOut.capacity() * 2)
                    } else {
                        flushNet()
                        rawOut.flush()
                        if (result.status == SSLEngineResult.Status.CLOSED) throw SSLException("O outro lado recusou a conexão segura.")
                    }
                    if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.FINISHED) break
                    engine.handshakeStatus
                }
                SSLEngineResult.HandshakeStatus.NEED_UNWRAP, SSLEngineResult.HandshakeStatus.NEED_UNWRAP_AGAIN -> {
                    appIn.compact()
                    val result = try { engine.unwrap(netIn, appIn) } finally { appIn.flip() }
                    when (result.status) {
                        SSLEngineResult.Status.BUFFER_UNDERFLOW -> if (!fillNet()) throw EOFException("A conexão caiu durante o aperto de mão.")
                        SSLEngineResult.Status.BUFFER_OVERFLOW -> {
                            val bigger = ByteBuffer.allocate(appIn.remaining() + engine.session.applicationBufferSize)
                            bigger.put(appIn)
                            bigger.flip()
                            appIn = bigger
                        }
                        SSLEngineResult.Status.CLOSED -> throw SSLException("O outro lado recusou a conexão segura.")
                        else -> {}
                    }
                    if (result.handshakeStatus == SSLEngineResult.HandshakeStatus.FINISHED) break
                    engine.handshakeStatus
                }
                SSLEngineResult.HandshakeStatus.NEED_TASK -> {
                    while (true) engine.delegatedTask?.run() ?: break
                    engine.handshakeStatus
                }
                else -> engine.handshakeStatus
            }
        }
    }

    companion object {
        /** Monta o contexto TLS com a chave e o certificado deste aparelho. */
        fun serverContext(key: PrivateKey, certificate: X509Certificate): SSLContext {
            val password = CharArray(0)
            val store = try {
                KeyStore.getInstance("PKCS12").apply { load(null, null) }
            } catch (e: Exception) {
                KeyStore.getInstance(KeyStore.getDefaultType()).apply { load(null, null) }
            }
            store.setKeyEntry("ponte", key, password, arrayOf(certificate))
            val factory = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm())
            factory.init(store, password)
            return SSLContext.getInstance("TLS").apply { init(factory.keyManagers, null, null) }
        }

        /**
         * Faz o TLS como servidor. [alreadyRead] são os bytes do começo da conexão que já foram lidos
         * (o primeiro byte, usado para reconhecer o tipo de conexão).
         */
        fun accept(context: SSLContext, input: InputStream, output: OutputStream, alreadyRead: ByteArray = ByteArray(0)): EngineTls {
            val engine = context.createSSLEngine()
            engine.useClientMode = false
            engine.needClientAuth = false
            engine.enabledProtocols = engine.supportedProtocols.filter { it == "TLSv1.2" || it == "TLSv1.3" }.toTypedArray()
            val tls = EngineTls(engine, input, output)
            if (alreadyRead.isNotEmpty()) {
                tls.netIn.clear()
                if (tls.netIn.capacity() < alreadyRead.size) tls.netIn = ByteBuffer.allocate(alreadyRead.size * 2)
                tls.netIn.put(alreadyRead)
                tls.netIn.flip()
            }
            try {
                tls.handshake()
            } catch (e: Exception) {
                tls.close()
                throw e
            }
            return tls
        }
    }
}
