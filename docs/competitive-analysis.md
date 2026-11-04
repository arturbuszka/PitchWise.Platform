# PitchWise — analiza konkurencji, cel produktu i kolejne kroki

> Dokument analityczny. Odpowiada na pytanie: czego nam brakuje względem platform jak
> Second Spectrum / Genius Sports, Stats Perform (Opta) i Metrica Sports — i gdzie realnie
> celować. Stan na 2026-07-11.

---

## TL;DR

- To są **dwie różne ligi**, nie jedna. Second Spectrum ≠ Metrica. Różnią się infrastrukturą,
  nie jakością kodu.
- PitchWise jest **jednokamerowy**, więc jego realny konkurent to **liga coachingowa
  (Metrica-klasa)**, nie elitarna (Second Spectrum). To nie jest wybór — to fizyka. Z jednej
  kamery nie zbudujesz tego, co rig wielokamerowy.
- Do Metrica-klasy jesteśmy **architektonicznie blisko**: mamy detekcję, tracking, rejestrację
  boiska i silnik stanu meczu. Brakuje głównie **human-in-the-loop**, **warstwy modeli** i
  **wizualizacji**.
- Największa luka produktowa to **korekta trackingu przez operatora** — na tym stoi Metrica,
  a my celujemy w pełny automat, który przy jednej kamerze zawsze będzie gubił tożsamości.

---

## 1. Dwie ligi — kluczowe rozróżnienie

### Liga elitarna: Second Spectrum / Genius Sports / Stats Perform / TRACAB

Oficjalni dostawcy danych dla NBA, Premier League, MLS. Metoda:

- **Rig wielokamerowy zainstalowany na stałe w stadionie** — 2–3+ kamery zsynchronizowane
  sprzętowo, kalibracja stała. To NIE transmisja TV, tylko dedykowana infrastruktura kosztująca
  setki tysięcy dolarów na stadion.
- **3D pose tracking** — nie tylko pozycja (x, y), ale poza ciała, setki punktów na powierzchni
  zawodnika, 25 razy na sekundę. Wymaga triangulacji z wielu kamer — **z jednej kamery fizycznie
  niemożliwe**.
- Produkują dwa rodzaje danych naraz:
  - **tracking data** — ciągłe pozycje wszystkich zawodników i piłki,
  - **event data** — Opta rejestruje ~60 typów zdarzeń, do 3000 akcji na mecz, częściowo ręcznie
    anotowane przez przeszkolonych analityków.

### Liga coachingowa: Metrica Sports / Veo / Pixellot

Narzędzia dla klubów amatorskich, półprofesjonalnych i analityków. Metoda:

- **Jedna kamera** — broadcast, Veo, Pixellot, a nawet smartfon. **Dokładnie nasz przypadek.**
- **Semi-automatyczny tracking** z jednej kamery + operator poprawiający błędy.
- Pozycje **2D na boisku** (przez homografię), bez 3D pose.
- Model biznesowy Metrica: darmowy tier + płatne $11 / $33 / $88 miesięcznie. Auto-tracking
  dopiero w najwyższym tierze ($88, "Advanced").

### Wniosek

Budujemy jednokamerowo, więc porównanie do Second Spectrum jest **kategorią błędu** — to jak
porównywać rower do pociągu. Realny konkurent i realny cel to **Metrica-klasa**.

---

## 2. Mapa stacku — kto czego używa i gdzie jesteśmy

| Warstwa | Elitarni (2nd Spectrum) | Coaching (Metrica) | PitchWise dziś |
|---|---|---|---|
| Wejście | rig wielokamerowy w stadionie | 1 kamera (broadcast/Veo) | 1 kamera ✓ *(ten sam problem co Metrica)* |
| Detekcja | własne modele CV | własne modele CV | YOLO ONNX ✓ |
| Tracking | wielokamerowa triangulacja 3D | 2D + operator poprawia | ByteTrack + OSNet ReID ✓ *(stride 5 psuje ID)* |
| Rejestracja boiska | kalibracja stała (rig) | auto field tracking / operator | **zbudowane** — YOLO-pose keypoints *(brak wag)* |
| Pozycje | 3D pose, 25×/s | 2D x,y na boisku | 2D x,y ✓ *(gdy homografia działa)* |
| Warstwa zdarzeń | tracking + event data (Opta) | kodowanie + auto-eventy | **zbudowany silnik** — posiadanie, podania |
| Modele | xG, xT, pitch control, pressing | część z powyższych + wizualizacje | brak *(architektura gotowa)* |
| Human-in-the-loop | armia analityków | **operator poprawia tracking** | brak *(pełny automat)* |
| Output | oficjalne dane dla lig | wideo + statystyki + wizualizacje | zdarzenia + time-on-pitch |

---

## 3. Czego brakuje — osiągalne z jednej kamery

To jest realny backlog. Wszystko poniżej da się zrobić jednokamerowo, jednym zespołem.

1. **Human-in-the-loop / korekta trackingu.**
   Sekret Metrica, którego większość projektów hobbystycznych nie dostrzega: oni **nie polegają
   na w pełni automatycznym trackingu**. Dają operatorowi narzędzie do szybkiej poprawy błędów ID.
   My celujemy w pełną automatyzację, która przy jednej kamerze zawsze będzie gubić tożsamości
   (zmierzone: przy stride 5 tylko 15% detekcji dostaje stabilne ID). **To prawdopodobnie
   największa pojedyncza luka produktowa** — różnica między "demo" a "narzędziem, które ktoś kupi".

2. **Warstwa modeli analitycznych.**
   Mamy `WorldState` (pozycje, posiadanie) — to jest dokładnie wejście, na którym stoją modele:
   - **xT (Expected Threat)** — wartość każdej pozycji piłki na boisku,
   - **pitch control** — czyj jest każdy metr boiska (funkcja pozycji i prędkości zawodników),
   - **pressing / PPDA** — intensywność nacisku,
   - **xG** — prawdopodobieństwo gola dla strzału.
   To są funkcje czyste nad `WorldState`. Architektura jest pod nie gotowa — brakuje tylko kodu.

3. **Persystencja tracking data + eksport.**
   Elitarni sprzedają *dane*. Dziś zapisujemy zdarzenia i time-on-pitch, ale nie ciągłe pozycje
   w formacie, który ktoś zaimportuje. Eksport typu SportVU/TRACAB otwiera integracje.

4. **Wizualizacje.**
   Heatmapy, mapy podań, sieci pasowe, timeline zdarzeń, mapy strzałów. Mamy dane; brakuje
   warstwy prezentacji. Frontend (Next.js) istnieje, ale te widoki — nie.

5. **Stride 1–2 dla trackingu.**
   Zmierzony blocker (stride 1 → 68% ID, stride 5 → 15%). Warunek wstępny dla wszystkiego powyżej —
   bez tego posiadanie i tak nie działa na pełnym meczu.

---

## 4. Czego brakuje — sufit jednej kamery (świadomie NIE robić)

To jest fizycznie poza zasięgiem jednej kamery. Gonienie tego to spalony czas.

1. **3D pose** (poza ciała, orientacja bioder, wysokość skoku) — wymaga triangulacji z wielu
   kamer.
2. **Wysokość piłki (Z)** — homografia płaszczyzna→płaszczyzna daje Z=0. Dośrodkowania, woleje,
   główki — trajektoria 3D poza zasięgiem.
3. **Pełne pokrycie boiska w każdej chwili** — kamera transmisyjna pokazuje ~⅓ boiska; zawodnicy
   poza kadrem nie istnieją w danych. Rig widzi całe boisko zawsze. To fundamentalnie ogranicza
   kompletność danych — nie policzymy pełnej formacji ani pełnego pitch control, gdy połowa
   drużyny jest poza kadrem.
4. **Dokładność sub-metrowa i niska latencja live** — ich dane są oficjalne dla lig, bo dokładne
   co do centymetrów i mają latencję < 1 s. Broadcast + homografia to metry błędu.

---

## 5. Cel produktu

**Celować w ligę coachingową (Metrica-klasa), nie elitarną.**

Metrica-klasa jest osiągalna jednokamerowo i jednym zespołem. Second Spectrum to nie konkurent —
to inna kategoria infrastruktury. Produkt: narzędzie do analizy wideo dla klubów
amatorskich/półprofesjonalnych, które z jednej kamery (broadcast/Veo/smartfon) daje tracking,
zdarzenia, statystyki i wizualizacje — z operatorem poprawiającym tracking tam, gdzie automat
zawodzi.

---

## 6. Kolejne kroki (kolejność budująca produkt, nie ciekawostkę)

1. **Napraw stride (1–2).** Warunek wstępny, zmierzony blocker. Bez tego reszta nie działa na
   pełnym meczu.
2. **Podłącz model boiska.** Część 2 (rejestracja) jest zbudowana i zweryfikowana — brakuje tylko
   pliku wag z Roboflow. Odblokowuje pozycje w metrach.
3. **Human-in-the-loop na tracking.** Różnica między "demo" a "produktem". Operator poprawia ID
   tam, gdzie automat gubi. Na tym stoi Metrica.
4. **Warstwa modeli nad WorldState.** Zacząć od **pitch control** i **xT** — najwięcej wartości
   analitycznej na jednostkę kodu, a architektura jest pod nie gotowa.
5. **Wizualizacje** — heatmapy, mapy podań, timeline zdarzeń. To jest to, co coach widzi i za co
   płaci.

**Świadomie nie robić:** 3D pose, wysokość piłki, pełne pokrycie boiska, dokładność oficjalnych
danych. To sufit jednej kamery.

---

## Źródła

- [Second Spectrum / Genius Sports — optical tracking](https://news.geniussports.com/genius-sports-acquires-second-spectrum-the-official-data-tracking-and-analytics-provider-of-the-epl-nba-and-mls/)
- [Multi-camera vs single broadcast camera (e-con Systems)](https://www.e-consystems.com/blog/camera/applications/how-multi-camera-systems-are-used-in-sports-broadcasting/)
- [Metrica Sports — automatic field & player tracking](https://www.metrica-sports.com/help-center/playbase-fundamentals/automatic-player-tracking)
- [Metrica Sports — pricing](https://www.metrica-sports.com/plans)
- [Event data vs tracking data; xG / xT / pitch control](https://www.statsperform.com/products/opta-vision/)
- [Real-time localization of a soccer ball from a single camera (arXiv)](https://arxiv.org/pdf/2506.07981)
