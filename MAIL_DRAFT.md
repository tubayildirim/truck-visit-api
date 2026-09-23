# Mail Draft — Truck Visit Management API

Konu: Truck Visit Management API — Case Submission

---

Hi Damla, Hi İlker,

Repo hazır: https://github.com/tubayildirim/truck-visit-api

README kurulum adımlarını içeriyor; ARCHITECTURE.md mimari kararların gerekçesini.
Kapasiteyi hesap yapmak yerine gerçek Postgres'te 1 milyon satır seed'leyip ölçtüm —
sonuçlar ARCHITECTURE §9'da var, dahil iki sürpriz: movement filtreleri için yazdığım
iki index planner tarafından kullanılmadı ve bu aslında doğru karar, neden orada açıklanmış.

Case'de istenmeyen üç şey ekledim:

- **Hash-chained audit trail** (ADR-013): DB trigger ve application guard tamperı önlüyor,
  ama biri superuser bağlantısıyla triggeri devre dışı bırakıp bir satırı editleyip yeniden
  açabilir. Chain bu farkı kapatıyor — harici referansa gerek yok, break zincirden okunuyor.

- **Movement lifecycle gate** (ADR-014): Hareketleri kayıt altına almadan bir visit
  Completed'a geçilebilmesi durumunda kargo hiç taşınmamış ama taşınmış gibi görünen
  kayıtlar oluşur. Bunu bir uyarı değil lifecycle kuralı olarak modelledim.

- **PostgreSQL RLS** (ADR-016): Application layer filtresinin yanında DB katmanında da
  tenant isolation. Tek katman "herkes bu kuralı hatırlar" varsayımına dayanıyor, ki bu
  yeterli değil.

148 test, CI her push'ta gerçek Postgres container'ına karşı çalışıyor.

Konuşmak istersen hazırım.

Tuba

---

## NOTLAR (mail atmadan önce sil)

Değişiklikler önceki maile göre:
- "I went a bit beyond" → neden eklediğini doğrudan söyle, özür dileme tonu yok
- "they felt like real gaps" → "felt" yerine teknik gerekçe
- İlk paragraf daha hızlı geçiyor, detay ikinci paragrafa taşındı
- tr-TR testi ve race condition hikayesini sözlü sunuma sakla — maillerde değil
