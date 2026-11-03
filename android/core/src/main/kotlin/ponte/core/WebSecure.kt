package ponte.core

import java.io.ByteArrayOutputStream
import java.io.DataInputStream
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.math.BigInteger
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.PrivateKey
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.ECPoint
import java.security.spec.ECPublicKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Canal cifrado do Ponte no navegador, do lado de quem é acessado: o mesmo de WebChannel.cs no Windows.
 *
 *   navegador → aqui: "WPONTE1" | chave efêmera ECDH (65 bytes) | 32 bytes aleatórios
 *   aqui → navegador: chave efêmera (65) | tamanho do certificado (2 bytes) | certificado | assinatura (64, r||s)
 *
 * A assinatura é da chave deste aparelho (a do certificado, da qual sai o ID) sobre
 * "ponte-web-1\n" | SHA-256(mensagem do navegador) | chave efêmera daqui | SHA-256(certificado).
 * Chaves: HKDF-SHA256 do segredo ECDH, sal = SHA-256 das duas mensagens, informação "ponte-web-1 c2s" ou "s2c".
 * Registros: tamanho do cifrado (4 bytes, big-endian) | AES-256-GCM com nonce = 4 zeros | contador de 8 bytes.
 */
class WebSecure private constructor(rawIn: InputStream, private val rawOut: OutputStream, receiveSecret: ByteArray, sendSecret: ByteArray) {
    private val inData = DataInputStream(rawIn)
    private val receiveKey = SecretKeySpec(receiveSecret, "AES")
    private val sendKey = SecretKeySpec(sendSecret, "AES")
    private var receiveCounter = 0L
    private var sendCounter = 0L
    private val pending = ByteArrayOutputStream()
    private var plain = ByteArray(0)
    private var plainOffset = 0

    val input: InputStream = object : InputStream() {
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) <= 0) -1 else one[0].toInt() and 0xFF
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            synchronized(this) {
                while (plainOffset >= plain.size) {
                    if (!nextRecord()) return -1
                }
                val n = minOf(len, plain.size - plainOffset)
                System.arraycopy(plain, plainOffset, b, off, n)
                plainOffset += n
                return n
            }
        }

        override fun close() = rawOut.close()
    }

    // O canal de mensagens escreve cabeçalho e conteúdo separados e chama flush no fim de cada mensagem:
    // juntar até o flush faz de cada mensagem um registro só.
    val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = synchronized(pending) { pending.write(b) }

        override fun write(b: ByteArray, off: Int, len: Int) = synchronized(pending) { pending.write(b, off, len) }

        override fun flush() = synchronized(pending) {
            val data = pending.toByteArray()
            pending.reset()
            var offset = 0
            while (offset < data.size) {
                val length = minOf(MAX_PLAIN, data.size - offset)
                val cipher = Cipher.getInstance("AES/GCM/NoPadding")
                cipher.init(Cipher.ENCRYPT_MODE, sendKey, GCMParameterSpec(128, nonce(sendCounter++)))
                val sealed = cipher.doFinal(data, offset, length)
                rawOut.write(byteArrayOf((sealed.size ushr 24).toByte(), (sealed.size ushr 16).toByte(), (sealed.size ushr 8).toByte(), sealed.size.toByte()))
                rawOut.write(sealed)
                offset += length
            }
            rawOut.flush()
        }

        override fun close() = rawOut.close()
    }

    private fun nextRecord(): Boolean {
        val header = ByteArray(4)
        try {
            inData.readFully(header)
        } catch (e: EOFException) {
            return false
        }
        val length = ((header[0].toInt() and 0xFF) shl 24) or ((header[1].toInt() and 0xFF) shl 16) or
            ((header[2].toInt() and 0xFF) shl 8) or (header[3].toInt() and 0xFF)
        if (length < TAG || length > MAX_PLAIN + TAG) throw IOException("Registro com tamanho inválido.")
        val sealed = ByteArray(length)
        inData.readFully(sealed)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, receiveKey, GCMParameterSpec(128, nonce(receiveCounter++)))
        plain = try {
            cipher.doFinal(sealed)
        } catch (e: java.security.GeneralSecurityException) {
            throw IOException("Registro adulterado.")
        }
        plainOffset = 0
        return true
    }

    companion object {
        val MAGIC = "WPONTE1".toByteArray(Charsets.US_ASCII)
        const val CLIENT_HELLO_LENGTH = 7 + 65 + 32
        private const val MAX_PLAIN = 1 shl 20
        private const val TAG = 16

        private fun nonce(counter: Long): ByteArray {
            val n = ByteArray(12)
            for (i in 0 until 8) n[4 + i] = (counter ushr (56 - 8 * i)).toByte()
            return n
        }

        /**
         * O aperto de mão do lado de quem é acessado. [first] são os bytes já lidos do começo da conexão
         * (no mínimo o "W").
         */
        fun accept(input: InputStream, output: OutputStream, first: ByteArray, key: PrivateKey, certificate: ByteArray): WebSecure {
            val hello = ByteArray(CLIENT_HELLO_LENGTH)
            System.arraycopy(first, 0, hello, 0, first.size)
            DataInputStream(input).readFully(hello, first.size, CLIENT_HELLO_LENGTH - first.size)
            if (!hello.copyOfRange(0, 7).contentEquals(MAGIC)) throw IOException("Início de conexão desconhecido.")
            if (hello[7] != 0x04.toByte()) throw IOException("Chave efêmera inválida.")

            val generator = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }
            val ephemeral = generator.generateKeyPair()
            val ownPublic = ephemeral.public as ECPublicKey
            val clientPoint = ECPoint(BigInteger(1, hello.copyOfRange(8, 40)), BigInteger(1, hello.copyOfRange(40, 72)))
            val clientKey = KeyFactory.getInstance("EC").generatePublic(ECPublicKeySpec(clientPoint, ownPublic.params))
            val secret = KeyAgreement.getInstance("ECDH").run {
                init(ephemeral.private)
                doPhase(clientKey, true)
                generateSecret()
            }

            val ownPoint = byteArrayOf(0x04) + SelfSignedCertificate.fixed(ownPublic.w.affineX) + SelfSignedCertificate.fixed(ownPublic.w.affineY)
            val signed = "ponte-web-1\n".toByteArray(Charsets.US_ASCII) + Crypto.sha256(hello) + ownPoint + Crypto.sha256(certificate)
            val signature = HostCrypto.sign(key, signed)
            val serverHello = ownPoint + byteArrayOf((certificate.size shr 8).toByte(), certificate.size.toByte()) + certificate + signature
            output.write(serverHello)
            output.flush()

            val salt = Crypto.sha256(hello, serverHello)
            val c2s = hkdf(secret, salt, "ponte-web-1 c2s")
            val s2c = hkdf(secret, salt, "ponte-web-1 s2c")
            secret.fill(0)
            return WebSecure(input, output, c2s, s2c)
        }

        /** HKDF-SHA256 com saída de 32 bytes (um bloco só). */
        internal fun hkdf(secret: ByteArray, salt: ByteArray, info: String): ByteArray {
            val extract = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(salt, "HmacSHA256")) }
            val prk = extract.doFinal(secret)
            val expand = Mac.getInstance("HmacSHA256").apply { init(SecretKeySpec(prk, "HmacSHA256")) }
            expand.update(info.toByteArray(Charsets.US_ASCII))
            expand.update(1)
            return expand.doFinal()
        }
    }
}
