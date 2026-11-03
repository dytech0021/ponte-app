package ponte.core

import java.io.ByteArrayOutputStream
import java.math.BigInteger
import java.security.GeneralSecurityException
import java.security.KeyFactory
import java.security.KeyPair
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.Signature
import java.security.cert.CertificateFactory
import java.security.cert.X509Certificate
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec
import java.text.SimpleDateFormat
import java.util.Base64
import java.util.Date
import java.util.Locale
import java.util.TimeZone

/** As contas do lado de quem é acessado, as mesmas do programa do Windows (Security.cs). */
object HostCrypto {
    /** Uma chave autorizada é reconhecida pelo resumo inteiro dela, em hexadecimal maiúsculo (como no Windows). */
    fun keyHash(spki: ByteArray): String = Crypto.hex(Crypto.sha256(spki))

    /** Assina como o .NET: ECDSA P-256 com SHA-256, no formato r||s. */
    fun sign(key: PrivateKey, data: ByteArray): ByteArray {
        val signature = Signature.getInstance("SHA256withECDSA")
        signature.initSign(key)
        signature.update(data)
        return Crypto.derToP1363(signature.sign())
    }

    /** r||s (64 bytes, como o .NET e o navegador assinam) no DER que o Java confere. */
    fun p1363ToDer(signature: ByteArray): ByteArray {
        require(signature.size == 64) { "Assinatura com tamanho inválido." }
        fun integer(part: ByteArray): ByteArray {
            var start = 0
            while (start < part.size - 1 && part[start] == 0.toByte()) start++
            var value = part.copyOfRange(start, part.size)
            if (value[0] < 0) value = byteArrayOf(0) + value
            return byteArrayOf(0x02, value.size.toByte()) + value
        }
        val body = integer(signature.copyOfRange(0, 32)) + integer(signature.copyOfRange(32, 64))
        return byteArrayOf(0x30, body.size.toByte()) + body
    }

    /** A chave pública de quem acessa (SPKI) é P-256, ou null. */
    fun publicKey(spki: ByteArray): ECPublicKey? = try {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(spki)) as? ECPublicKey
        key?.takeIf { it.params.curve.field.fieldSize == 256 && it.encoded.contentEquals(spki) }
    } catch (e: GeneralSecurityException) {
        null
    } catch (e: IllegalArgumentException) {
        null
    }

    /** Confere a assinatura com que quem acessa prova ser dono da chave enviada no Hello. */
    fun verifyPairing(spki: ByteArray, nonce: ByteArray, certificateHash: ByteArray, signature: String?): Boolean {
        if (signature == null || signature.length > 256) return false
        val key = publicKey(spki) ?: return false
        return try {
            val raw = Base64.getDecoder().decode(signature)
            if (raw.size != 64) return false
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(key)
                update(Crypto.pairingMessage(nonce, certificateHash))
                verify(p1363ToDer(raw))
            }
        } catch (e: GeneralSecurityException) {
            false
        } catch (e: IllegalArgumentException) {
            false
        }
    }

    /** O verificador da senha (o que fica guardado), igual ao do Windows: PBKDF2-HMAC-SHA256 de 32 bytes. */
    fun verifier(password: String, salt: ByteArray, iterations: Int): ByteArray = Crypto.pbkdf2(password, salt, iterations)
}

/**
 * Certificado autoassinado ECDSA P-256 com "CN=Ponte", como o que o Ponte do Windows cria na primeira abertura.
 * Ninguém confere a cadeia: o ID sai da chave pública e a impressão digital é guardada por quem acessa.
 */
object SelfSignedCertificate {
    private val ECDSA_WITH_SHA256 = byteArrayOf(0x06, 0x08, 0x2A, 0x86.toByte(), 0x48, 0xCE.toByte(), 0x3D, 0x04, 0x03, 0x02)
    private val COMMON_NAME = byteArrayOf(0x06, 0x03, 0x55, 0x04, 0x03)

    fun create(pair: KeyPair, years: Int = 30, now: Date = Date(), random: SecureRandom = SecureRandom()): ByteArray {
        val serial = ByteArray(16).also { random.nextBytes(it) }
        serial[0] = (serial[0].toInt() and 0x7F or 0x01).toByte()
        val algorithm = tlv(0x30, ECDSA_WITH_SHA256)
        val name = tlv(0x30, tlv(0x31, tlv(0x30, COMMON_NAME + tlv(0x0C, "Ponte".toByteArray(Charsets.UTF_8)))))
        val notBefore = Date(now.time - 24L * 3600 * 1000)
        val notAfter = Date(now.time + years * 365L * 24 * 3600 * 1000)
        val tbs = tlv(
            0x30,
            tlv(0xA0, tlv(0x02, byteArrayOf(2))) +
                tlv(0x02, serial) +
                algorithm +
                name +
                tlv(0x30, time(notBefore) + time(notAfter)) +
                name +
                pair.public.encoded,
        )
        val signature = Signature.getInstance("SHA256withECDSA").run {
            initSign(pair.private)
            update(tbs)
            sign()
        }
        return tlv(0x30, tbs + algorithm + tlv(0x03, byteArrayOf(0) + signature))
    }

    fun parse(der: ByteArray): X509Certificate =
        CertificateFactory.getInstance("X.509").generateCertificate(der.inputStream()) as X509Certificate

    // Datas até 2049 em UTCTime e dali em diante em GeneralizedTime, como manda a RFC 5280.
    private fun time(date: Date): ByteArray {
        val utc = TimeZone.getTimeZone("UTC")
        val year = java.util.Calendar.getInstance(utc).apply { time = date }.get(java.util.Calendar.YEAR)
        val (tag, pattern) = if (year < 2050) 0x17 to "yyMMddHHmmss'Z'" else 0x18 to "yyyyMMddHHmmss'Z'"
        val text = SimpleDateFormat(pattern, Locale.US).apply { timeZone = utc }.format(date)
        return tlv(tag, text.toByteArray(Charsets.US_ASCII))
    }

    private fun tlv(tag: Int, content: ByteArray): ByteArray {
        val out = ByteArrayOutputStream(content.size + 6)
        out.write(tag)
        val n = content.size
        when {
            n < 0x80 -> out.write(n)
            n < 0x100 -> { out.write(0x81); out.write(n) }
            n < 0x10000 -> { out.write(0x82); out.write(n shr 8); out.write(n and 0xFF) }
            else -> { out.write(0x83); out.write(n shr 16); out.write((n shr 8) and 0xFF); out.write(n and 0xFF) }
        }
        out.write(content)
        return out.toByteArray()
    }

    /** O número inteiro sem sinal, com exatamente [size] bytes (o BigInteger às vezes sobra ou falta um). */
    internal fun fixed(value: BigInteger, size: Int = 32): ByteArray {
        val raw = value.toByteArray()
        return when {
            raw.size == size -> raw
            raw.size > size -> raw.copyOfRange(raw.size - size, raw.size)
            else -> ByteArray(size - raw.size) + raw
        }
    }
}
