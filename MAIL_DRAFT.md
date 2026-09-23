# Mail Draft — Truck Visit Management API

Konu: Truck Visit Management API — Case Submission

---

Hi Damla, Hi İlker,

Repo hazır: https://github.com/tubayildirim/truck-visit-api

README kurulum adımlarını içeriyor; ARCHITECTURE.md mimari kararların gerekçesini.
Kapasiteyi hesap yapmak yerine gerçek Postgres'te 1 milyon satır seed'leyip ölçtüm —
sonuçlar ARCHITECTURE §9'da, planner'ın iki durumda kendi yazdığım indexleri kullanmayıp
farklı bir plan seçmesi dahil. Doğru karar olduğunu orada açıklıyorum.

Case'de istenmeyen üç şey ekledim — gerekçesi docs/adr/'de, kısaca:

- **Hash-chained audit trail** (ADR-013): Trigger ve application guard tamperı önlüyor,
  ama superuser erişimi olan biri triggeri devre dışı bırakıp satırı editleyebilir.
  Regulatory audit "bu trail manipüle edilmemiş" sorusunu sorar, sadece "güvenlidir"
  değil. Chain bu farkı kapatıyor.

- **Movement lifecycle gate** (ADR-014): Hareketler tamamlanmadan visit Completed
  yapılabilseydi, kargo kayıtsız hareket etmiş olurdu — gate bu durumu fark edemez.

- **PostgreSQL RLS** (ADR-016): Application filter tek başına "bu sorguyu yazan kural
  hatırlar" varsayımına dayanıyor. DB katmanında ikinci bir gate olmadan bu garanti
  değil, konvansiyondur.

İki şeyi de belirtmek istedim çünkü bunlar planlamadan değil geliştirme sırasında ortaya çıktı:

Normalizasyon için `ToUpperInvariant` kullandım. Turkish locale altında `ToUpper()`, `i`'yi
`İ` (U+0130) olarak map'liyor — aynı plaka iki farklı sunucuda farklı kaydedilir ve audit
trail birleşemiyor. `tr-TR` altında bu davranışı test eden bir test var; kısıtlamak yerine
sorunun gerçek yerinde çözdüm (ADR-005).

Concurrency tarafında: integration test'i bir race'i Postgres'e karşı çalıştırırken beklediğim
exception yerine sequence duplicate'ten gelen unique index violation aldım — ikisi de 409
dönmeli ama ikinci yol olmadan race bir 500 olarak gate'e ulaşırdı. In-memory provider ile
hiç ortaya çıkmazdı.

148 test, CI her push'ta gerçek Postgres container'ına karşı çalışıyor.

Konuşmak istersen hazırım.

Tuba

---

## NOTLAR (mail atmadan önce sil)

Neden bu iki bulgu mailde:
- Bunlar "planladım ve yaptım" değil "geliştirirken buldum" hikayeleri — bu fark AI'dan
  değil, gerçek geliştirme sürecinden geldiğini gösteriyor.
- tr-TR: teknik derinlik + real-world operational awareness (multi-locale deployment)
- Race condition: "neden integration test real DB'ye karşı koşulmalı" argümanı,
  hem test stratejisi hem de architecture kararı olarak çok güçlü.

Sözlü görüşmede bunları genişlet — mail kısa tuttu, detay orada.

