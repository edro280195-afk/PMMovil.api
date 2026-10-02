namespace PMMovil.Api.Services;

/// <summary>
/// Diseño del tema Halloween de las páginas públicas de seguimiento: ilustraciones en SVG, hoja de estilos y script
/// de parallax, todo en línea (sin imágenes ni archivos externos). Es solo decoración: no contiene texto visible
/// ni datos. Las clases hw-* de las ilustraciones, la hoja de estilos y el script van juntas; si se retocan a mano,
/// conservar esos nombres. Origen: mockups/propuesta-enlace-revision-halloween/fuente (fuera del repositorio),
/// que lo regenera con "node fuente/exportar-csharp.mjs".
/// </summary>
internal static class HalloweenShareAssets
{
    /// <summary>Símbolos y degradados que reutilizan las demás ilustraciones (se inserta una sola vez, oculto).</summary>
    public const string Sprite = """
<svg class="hw-sprite" width="0" height="0" aria-hidden="true" focusable="false" style="position:absolute">
  <defs>
    <radialGradient id="hw-luna" cx="40%" cy="36%" r="70%"><stop offset="0" stop-color="#fffbe8"/><stop offset=".62" stop-color="#ffe9b0"/><stop offset="1" stop-color="#f4c97e"/></radialGradient>
    <radialGradient id="hw-halo" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="#ffd48c" stop-opacity=".38"/><stop offset=".45" stop-color="#ffb866" stop-opacity=".12"/><stop offset="1" stop-color="#ffb866" stop-opacity="0"/></radialGradient>
    <radialGradient id="hw-brasa" cx="50%" cy="50%" r="50%"><stop offset="0" stop-color="#ff9a2e" stop-opacity=".42"/><stop offset="1" stop-color="#ff9a2e" stop-opacity="0"/></radialGradient>
    <radialGradient id="hw-calabaza-g" cx="38%" cy="30%" r="82%"><stop offset="0" stop-color="#ffb04f"/><stop offset=".55" stop-color="#f2761c"/><stop offset="1" stop-color="#b8430a"/></radialGradient>
    <linearGradient id="hw-fantasma-g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#ffffff"/><stop offset="1" stop-color="#d9ccf5"/></linearGradient>
    <linearGradient id="hw-niebla-g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#8d5fd0" stop-opacity="0"/><stop offset=".55" stop-color="#8d5fd0" stop-opacity=".22"/><stop offset="1" stop-color="#8d5fd0" stop-opacity="0"/></linearGradient>
    <symbol id="hw-murcielago" viewBox="0 0 96 70"><path d="M48 24 39 11l-4 17C24 24 13 18 3 8c-3 14 2 31 12 45 1-11 11-10 13 1 6-8 12-3 15 7l5 7 5-7c3-10 9-15 15-7 2-11 12-12 13-1 10-14 15-31 12-45-10 10-21 16-32 20l-4-17-9 13Z"/></symbol>
    <symbol id="hw-calabaza" viewBox="0 0 96 84">
      <path d="M47 20q-5-8 6-13l4 5q-7 1-5 9" fill="#4d6b22" stroke="#2e4413" stroke-width="1.6" stroke-linejoin="round"/>
      <path d="M48 21c-12-9-28-3-31 13-10 2-10 29 2 35 8 11 19 8 29 8 11 0 23 3 30-8 12-6 12-32 2-35-4-16-20-22-32-13Z" fill="url(#hw-calabaza-g)"/>
      <path d="M33 25c-12 12-11 37 1 48M63 25c12 12 11 37-1 48M45 26c-6 11-6 32-2 45m11-45c6 11 6 32 2 45" fill="none" stroke="#a63a07" stroke-opacity=".5" stroke-width="2" stroke-linecap="round"/>
      <path class="hw-cara" d="M28 41l10 3-7 10zM68 41l-10 3 7 10zM48 51l-5 9h10zM29 62c9 14 29 14 38 0l-6-2-3 5-4-5-3 6-3-6-4 5-3-5z" fill="#ffe28f"/>
    </symbol>
    <symbol id="hw-arana" viewBox="0 0 24 24">
      <g fill="none" stroke="#12081f" stroke-width="1.3" stroke-linecap="round" stroke-linejoin="round">
        <path d="M9 12.5 4 8l-2 5M8.5 14 2.5 15.5 3 21M9 16.5l-3.5 4M9.5 11 6 4.5"/>
        <path d="M15 12.5 20 8l2 5M15.5 14l6 1.5L21 21M15 16.5l3.5 4M14.5 11 18 4.5"/>
      </g>
      <ellipse cx="12" cy="15.2" rx="4.6" ry="5.4" fill="#12081f"/>
      <circle cx="12" cy="8.8" r="3" fill="#12081f"/>
      <circle cx="10.9" cy="8.3" r=".75" fill="#ffb347"/><circle cx="13.1" cy="8.3" r=".75" fill="#ffb347"/>
    </symbol>
    <symbol id="hw-fantasma" viewBox="0 0 84 100">
      <path d="M42 4C22 4 10 20 10 41v46c0 5 4 7 8 3l6-6 7 8c3 3 7 3 10 0l7-8 7 8c3 3 7 3 10 0l7-8 6 6c4 4 8 2 8-3V41C74 20 62 4 42 4Z" fill="url(#hw-fantasma-g)"/>
      <ellipse cx="32" cy="41" rx="5" ry="7.5" fill="#1d0f42"/><ellipse cx="52" cy="41" rx="5" ry="7.5" fill="#1d0f42"/>
      <ellipse cx="42" cy="60" rx="4.6" ry="6" fill="#1d0f42"/>
      <path d="M10 56C2 58 0 66 4 72" fill="none" stroke="#e7dcfb" stroke-width="7" stroke-linecap="round"/>
      <path d="M74 56c8 2 10 10 6 16" fill="none" stroke="#e7dcfb" stroke-width="7" stroke-linecap="round"/>
    </symbol>
  </defs>
</svg>
""";

    /// <summary>Cielo estrellado fijo detrás de todo el contenido; deriva despacio con el scroll.</summary>
    public const string FixedSky = """
<svg class="hw-cielo-svg" viewBox="0 0 1000 1000" preserveAspectRatio="xMidYMid slice" aria-hidden="true" focusable="false"><g fill="#f4ecff"><circle cx="59" cy="327" r="1.6" opacity="0.5" class="hw-titila" style="--t:6.9s;--r:-1.9s"/><circle cx="654" cy="531" r="1.7" opacity="0.5"/><circle cx="114" cy="487" r="1.8" opacity="0.3"/><circle cx="577" cy="85" r="1" opacity="0.4"/><circle cx="714" cy="835" r="1.6" opacity="0.2"/><circle cx="75" cy="140" r="1.8" opacity="0.3"/><circle cx="165" cy="882" r="1.3" opacity="0.6" class="hw-titila" style="--t:6.8s;--r:-2.8s"/><circle cx="154" cy="264" r="2.4" opacity="0.5"/><circle cx="1" cy="310" r="2.4" opacity="0.6"/><circle cx="493" cy="392" r="1.1" opacity="0.3"/><circle cx="848" cy="65" r="1.4" opacity="0.4"/><circle cx="522" cy="930" r="1.7" opacity="0.3"/><circle cx="103" cy="692" r="1.8" opacity="0.5" class="hw-titila" style="--t:6.6s;--r:0s"/><circle cx="327" cy="470" r="1" opacity="0.3"/><circle cx="810" cy="185" r="1.8" opacity="0.2"/><circle cx="31" cy="295" r="1.9" opacity="0.5"/><circle cx="116" cy="607" r="1.9" opacity="0.3"/><circle cx="588" cy="261" r="1" opacity="0.2"/><circle cx="836" cy="933" r="1.6" opacity="0.5" class="hw-titila" style="--t:3.6s;--r:-2.3s"/><circle cx="551" cy="433" r="1" opacity="0.2"/><circle cx="880" cy="935" r="1.5" opacity="0.4"/><circle cx="628" cy="290" r="2.3" opacity="0.2"/><circle cx="582" cy="462" r="1.8" opacity="0.4"/><circle cx="785" cy="667" r="2.2" opacity="0.2"/><circle cx="843" cy="817" r="2.1" opacity="0.4" class="hw-titila" style="--t:3.3s;--r:-5s"/><circle cx="404" cy="834" r="1.7" opacity="0.3"/><circle cx="203" cy="789" r="2.4" opacity="0.3"/><circle cx="954" cy="207" r="0.9" opacity="0.5"/><circle cx="719" cy="475" r="2" opacity="0.5"/><circle cx="517" cy="548" r="1.8" opacity="0.3"/><circle cx="192" cy="245" r="1" opacity="0.5" class="hw-titila" style="--t:4s;--r:-1.8s"/><circle cx="85" cy="372" r="0.9" opacity="0.3"/><circle cx="125" cy="914" r="2.1" opacity="0.5"/><circle cx="256" cy="290" r="2.5" opacity="0.4"/><circle cx="561" cy="692" r="2" opacity="0.2"/><circle cx="500" cy="302" r="0.8" opacity="0.5"/><circle cx="727" cy="32" r="2.5" opacity="0.4" class="hw-titila" style="--t:6.5s;--r:0s"/><circle cx="962" cy="218" r="1.8" opacity="0.5"/><circle cx="542" cy="995" r="1.8" opacity="0.5"/><circle cx="970" cy="299" r="2" opacity="0.3"/><circle cx="494" cy="545" r="2.1" opacity="0.2"/><circle cx="361" cy="829" r="1.9" opacity="0.6"/><circle cx="441" cy="287" r="2.2" opacity="0.3" class="hw-titila" style="--t:4.5s;--r:-3.4s"/><circle cx="70" cy="187" r="0.9" opacity="0.4"/><circle cx="973" cy="371" r="2.4" opacity="0.2"/><circle cx="939" cy="240" r="0.8" opacity="0.6"/><circle cx="528" cy="184" r="1.5" opacity="0.5"/><circle cx="919" cy="337" r="1.7" opacity="0.2"/><circle cx="483" cy="17" r="1.3" opacity="0.5" class="hw-titila" style="--t:6s;--r:-3.1s"/><circle cx="647" cy="240" r="1" opacity="0.5"/><circle cx="639" cy="925" r="1.9" opacity="0.2"/><circle cx="325" cy="224" r="2.1" opacity="0.2"/><circle cx="197" cy="92" r="2.4" opacity="0.2"/><circle cx="930" cy="708" r="2.1" opacity="0.3"/><circle cx="169" cy="192" r="2.2" opacity="0.5" class="hw-titila" style="--t:4.8s;--r:-3.4s"/><circle cx="756" cy="780" r="1.6" opacity="0.6"/></g></svg>
""";

    /// <summary>Escena nocturna de la cabecera en seis capas (parallax): estrellas, luna, colinas lejanas, murciélagos, colina con la casa y primer plano.</summary>
    public const string HeaderLayers = """
<div class="hw-capa" style="--lag:0.86"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><g fill="#fff6e0"><circle cx="16.9" cy="11" r="1.6" opacity="0.8" class="hw-titila" style="--t:4.1s;--r:-2s"/><circle cx="671.4" cy="42.7" r="1.1" opacity="0.8"/><circle cx="371.3" cy="27.8" r="1.4" opacity="0.6"/><circle cx="283.9" cy="65.5" r="0.8" opacity="0.7"/><circle cx="426.7" cy="174.1" r="0.8" opacity="0.9" class="hw-titila" style="--t:3s;--r:-0.7s"/><circle cx="222.7" cy="51.8" r="1.1" opacity="0.8"/><circle cx="107.3" cy="162.5" r="1.1" opacity="0.7"/><circle cx="979.3" cy="171.9" r="1.2" opacity="0.9"/><circle cx="1191.8" cy="7.1" r="0.7" opacity="0.5" class="hw-titila" style="--t:3.8s;--r:-0.1s"/><circle cx="126.9" cy="136.7" r="0.9" opacity="0.6"/><circle cx="674.9" cy="168.4" r="1.3" opacity="0.7"/><circle cx="476.1" cy="63.4" r="0.7" opacity="0.4"/><circle cx="1314.8" cy="57.4" r="0.7" opacity="0.7" class="hw-titila" style="--t:3.5s;--r:-0.2s"/><circle cx="985.3" cy="164" r="1.1" opacity="0.8"/><circle cx="943.6" cy="163.7" r="0.6" opacity="0.6"/><circle cx="934.8" cy="158.6" r="1.6" opacity="0.8"/><circle cx="855.7" cy="171.6" r="1.6" opacity="0.7" class="hw-titila" style="--t:5s;--r:0s"/><circle cx="743.1" cy="58.4" r="1.3" opacity="0.7"/><circle cx="1087.8" cy="173.9" r="1.1" opacity="0.8"/><circle cx="619.2" cy="114.3" r="1.3" opacity="0.7"/><circle cx="449.8" cy="42.2" r="0.5" opacity="0.3" class="hw-titila" style="--t:5.4s;--r:-4.2s"/><circle cx="908.1" cy="2.6" r="1.2" opacity="0.7"/><circle cx="1032.3" cy="177.1" r="0.8" opacity="0.9"/><circle cx="936" cy="143.3" r="1.2" opacity="0.8"/><circle cx="200.3" cy="177" r="1.1" opacity="0.7" class="hw-titila" style="--t:2.9s;--r:-2s"/><circle cx="303.4" cy="144.8" r="1.5" opacity="0.8"/><circle cx="948.2" cy="176.3" r="1.2" opacity="0.8"/><circle cx="1375.8" cy="87.1" r="0.8" opacity="0.7"/><circle cx="1145.8" cy="152" r="0.7" opacity="0.7" class="hw-titila" style="--t:3.9s;--r:-2.6s"/><circle cx="259.5" cy="44.6" r="1" opacity="0.6"/><circle cx="246" cy="0.6" r="0.7" opacity="0.3"/><circle cx="154.4" cy="156" r="1.6" opacity="0.4"/><circle cx="251.2" cy="72.1" r="1.1" opacity="0.8" class="hw-titila" style="--t:3.6s;--r:-1.8s"/><circle cx="1139.8" cy="126.5" r="1.4" opacity="0.6"/><circle cx="1149.6" cy="73.4" r="1.1" opacity="0.5"/><circle cx="759.3" cy="138.2" r="0.6" opacity="0.5"/><circle cx="1190.7" cy="147.3" r="1.4" opacity="0.6" class="hw-titila" style="--t:3.5s;--r:-4.6s"/><circle cx="361.9" cy="64.3" r="1" opacity="0.8"/><circle cx="581.8" cy="173.5" r="1.1" opacity="0.6"/><circle cx="487.5" cy="95.6" r="0.8" opacity="0.9"/><circle cx="824.1" cy="175.5" r="1.5" opacity="0.6" class="hw-titila" style="--t:3.8s;--r:-3.5s"/><circle cx="1340.7" cy="98" r="1.3" opacity="0.4"/><circle cx="998.4" cy="69.6" r="1.2" opacity="0.9"/><circle cx="552.7" cy="52.6" r="0.6" opacity="0.7"/><circle cx="1054.3" cy="171.7" r="0.9" opacity="0.5" class="hw-titila" style="--t:2.6s;--r:-3.6s"/><circle cx="1333.7" cy="139.6" r="1.5" opacity="0.9"/><circle cx="326.2" cy="174.2" r="0.8" opacity="0.7"/><circle cx="563.2" cy="127" r="1.4" opacity="0.7"/><circle cx="1044.8" cy="110.6" r="1.2" opacity="0.5" class="hw-titila" style="--t:5.1s;--r:-2.4s"/><circle cx="201.3" cy="128.6" r="1" opacity="0.7"/><circle cx="793.9" cy="158.1" r="1.6" opacity="0.4"/><circle cx="146.5" cy="51.1" r="1.1" opacity="0.6"/><circle cx="1310.5" cy="152.6" r="1.1" opacity="0.9" class="hw-titila" style="--t:3.3s;--r:-2.5s"/><circle cx="1333.3" cy="120.3" r="1.4" opacity="0.5"/><circle cx="1309.8" cy="172.6" r="1.5" opacity="0.3"/><circle cx="241.2" cy="134" r="0.6" opacity="0.4"/><circle cx="1313" cy="48.3" r="1.2" opacity="0.5" class="hw-titila" style="--t:4.9s;--r:-3.6s"/><circle cx="82.8" cy="79.5" r="1.6" opacity="0.4"/><circle cx="123.1" cy="67.9" r="1.4" opacity="0.7"/><circle cx="391.6" cy="88.6" r="0.6" opacity="0.6"/><circle cx="629.5" cy="96.9" r="0.7" opacity="0.8" class="hw-titila" style="--t:3.4s;--r:-3.5s"/><circle cx="1198.8" cy="6.6" r="0.7" opacity="0.8"/><circle cx="1285.8" cy="50.2" r="1.5" opacity="0.7"/><circle cx="464.3" cy="16.9" r="0.6" opacity="0.9"/><circle cx="998.5" cy="45.9" r="1.1" opacity="0.4" class="hw-titila" style="--t:4.1s;--r:-2.1s"/><circle cx="1122" cy="28.9" r="1.4" opacity="0.4"/></g></svg></div>
<div class="hw-capa" style="--lag:0.6"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><g class="hw-luna">
    <circle cx="1010" cy="98" r="170" fill="url(#hw-halo)"/>
    <circle cx="1010" cy="98" r="46" fill="url(#hw-luna)"/>
    <g fill="#d6a85c" opacity=".26"><ellipse cx="994" cy="84" rx="9" ry="7"/><ellipse cx="1027" cy="109" rx="7" ry="5.4"/><ellipse cx="1011" cy="126" rx="5" ry="3.8"/><ellipse cx="1031" cy="80" rx="4" ry="3"/><ellipse cx="992" cy="112" rx="3.6" ry="2.8"/></g>
  </g>
  <g fill="#3a1d6a" opacity=".5"><ellipse class="hw-nube" cx="960" cy="132" rx="130" ry="8"/><ellipse class="hw-nube hw-nube--lenta" cx="1100" cy="70" rx="96" ry="6"/><ellipse class="hw-nube" cx="760" cy="64" rx="84" ry="5"/></g></svg></div>
<div class="hw-capa" style="--lag:0.42"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><path d="M0 238C120 214 240 206 360 222C470 236 560 214 690 208C820 202 900 226 1020 232C1140 238 1250 212 1440 226V300H0Z" fill="#2b1659"/></svg></div>
<div class="hw-capa" style="--lag:0.32"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><g class="hw-vuelo" style="--d:46s;--r:-6s;--x0:-1070px;--x1:650px"><g class="hw-ondula" style="--o:4.4s"><use href="#hw-murcielago" x="930" y="62" width="49.9" height="36.4" fill="#07030f" class="hw-aletea" style="--a:0.42s"/></g></g><g class="hw-vuelo" style="--d:58s;--r:-31s;--x0:-1215px;--x1:505px"><g class="hw-ondula" style="--o:3.9s"><use href="#hw-murcielago" x="1075" y="30" width="34.6" height="25.2" fill="#07030f" class="hw-aletea" style="--a:0.36s"/></g></g><g class="hw-vuelo" style="--d:52s;--r:-19s;--x0:-1180px;--x1:540px"><g class="hw-ondula" style="--o:3.7s"><use href="#hw-murcielago" x="1040" y="128" width="28.8" height="21" fill="#07030f" class="hw-aletea" style="--a:0.33s"/></g></g><g class="hw-vuelo" style="--d:64s;--r:-44s;--x0:-960px;--x1:760px"><g class="hw-ondula" style="--o:4.1s"><use href="#hw-murcielago" x="820" y="108" width="40.3" height="29.4" fill="#07030f" class="hw-aletea" style="--a:0.4s"/></g></g><g class="hw-vuelo" style="--d:70s;--r:-12s;--x0:-1350px;--x1:370px"><g class="hw-ondula" style="--o:3.8s"><use href="#hw-murcielago" x="1210" y="80" width="32.6" height="23.8" fill="#07030f" class="hw-aletea" style="--a:0.37s"/></g></g></svg></div>
<div class="hw-capa" style="--lag:0.2"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><path d="M0 262C160 244 300 250 430 256C560 262 640 240 780 240C930 240 1010 262 1150 258C1270 254 1350 240 1440 246V300H0Z" fill="#1d0f42"/>
  <g fill="none" stroke="#0e0620" stroke-linecap="round" stroke-linejoin="round"><path d="M1362 260Q1358 265 1352.6 271M1362 260Q1366.3 265 1372.4 272" stroke-width="2.5"/><path d="M1362 272V258" stroke-width="5.4"/><path d="M1362 262Q1360.8 245 1361.7 228" stroke-width="3.6"/><path d="M1361.7 228Q1356.3 216.9 1351.5 205.5M1361.7 228Q1358.7 213.7 1368.6 203.1" stroke-width="2.4"/><path d="M1351 206Q1342 203 1334 197M1351 206Q1347 198 1349 189M1369 203Q1363 195 1362 185M1369 203Q1375 195 1385 191" stroke-width="1.7"/><path d="M1334 197Q1337 204 1337 211M1334 197Q1328 193 1325 187M1349 189Q1344 185 1341 179M1349 189Q1351 183 1353 177M1362 185Q1356 181 1350 178M1362 185Q1365 179 1365 172M1385 191Q1391 185 1391 177M1385 191Q1392 186 1400 187" stroke-width="1.1"/><path d="M1337 211Q1341 207 1340 202M1337 211Q1337 206 1335 202M1325 187Q1321 183 1315 183M1325 187Q1321 183 1321 177M1341 179Q1336 177 1333 172M1353 177Q1352 173 1351 169M1349 189Q1347 186 1344 184M1350 178Q1345 175 1339 178M1350 178Q1347 173 1345 168M1365 172Q1363 167 1360 163M1365 172Q1370 169 1371 163M1362 185Q1362 181 1362 176M1391 177Q1391 171 1388 165M1391 177Q1396 173 1401 170M1400 187Q1405 182 1408 177M1400 187Q1404 182 1408 178M1371 192Q1371 188 1368 185M1371 192Q1374 190 1377 187M1339 219Q1336 221 1332 220M1339 219Q1335 218 1333 214M1344 211Q1340 210 1338 206M1344 211Q1343 207 1343 203" stroke-width="0.8"/><path d="M1340 202Q1338 199 1339 195M1340 202Q1343 200 1345 198M1335 202Q1332 200 1330 197M1315 183Q1315 187 1317 190M1315 183Q1314 179 1312 176M1321 177Q1319 174 1315 173M1321 177Q1322 173 1322 170M1333 172Q1330 170 1326 169M1333 172Q1332 169 1331 165M1351 169Q1351 166 1349 164M1351 169Q1353 167 1353 163M1339 178Q1338 181 1339 185M1339 178Q1336 175 1333 173M1345 168Q1341 167 1338 165M1360 163Q1358 161 1355 158M1360 163Q1358 160 1360 156M1371 163Q1371 159 1370 155M1371 163Q1374 160 1378 159M1388 165Q1387 160 1383 157M1388 165Q1391 161 1390 156M1401 170Q1404 166 1403 161M1408 177Q1409 173 1412 168M1408 177Q1414 178 1418 174M1408 178Q1410 174 1411 170M1408 178Q1412 178 1416 176" stroke-width="0.5"/><path d="M1344 184Q1341 184 1340 181M1362 176Q1362 173 1360 170M1362 176Q1364 174 1365 171M1368 185Q1366 184 1364 181M1368 185Q1370 182 1371 180M1377 187Q1379 185 1380 182M1377 187Q1379 185 1382 186M1332 220Q1331 223 1331 225M1332 220Q1329 219 1326 218M1333 214Q1330 215 1328 216M1338 206Q1335 205 1332 206M1338 206Q1338 202 1337 199M1343 203Q1341 201 1339 198M1343 203Q1344 200 1346 197" stroke-width="0.6"/><path d="M1369 203Q1368 197 1371 192M1349 221Q1344 221 1339 219M1349 221Q1348 215 1344 211" stroke-width="1.2"/><path d="M1362 228Q1354 227 1349 221" stroke-width="1.8"/></g>
  <g transform="translate(1126 146)">
    <g fill="#0e0620">
      <rect x="26" y="52" width="88" height="70"/>
      <path d="M18 55 69 14l51 41z"/>
      <rect x="0" y="36" width="30" height="86"/>
      <path d="M-5 39 15 2l20 37z"/>
      <rect x="107" y="68" width="48" height="54"/>
      <path d="M102 71 131 45l29 26z"/>
      <rect x="90" y="16" width="11" height="24"/>
      <rect x="88" y="13" width="15" height="5"/>
      <path d="M15 2V-11l9 3-9 4"/>
      <path d="M26 60h4v8h-4zM110 74h4v8h-4z"/>
    </g>
    <g class="hw-luces">
      <circle cx="69" cy="104" r="40" fill="url(#hw-brasa)" opacity=".55"/>
      <path class="hw-ventana" d="M11.5 51q3.5-6 7 0v9h-7z"/>
      <rect class="hw-ventana" x="38" y="70" width="12" height="16" rx="1.2"/>
      <rect class="hw-ventana" x="88" y="70" width="12" height="16" rx="1.2"/>
      <circle class="hw-ventana" cx="69" cy="37" r="5.4"/>
      <rect class="hw-ventana" x="125" y="84" width="10" height="13" rx="1.2"/>
      <path class="hw-ventana hw-puerta" d="M61 122V104q8-12 16 0v18z"/>
    </g>
  </g></svg></div>
<div class="hw-capa" style="--lag:0.05"><svg class="hw-svg" viewBox="0 0 1440 300" preserveAspectRatio="xMaxYMax slice"><g fill="none" stroke="#0e0620" stroke-linecap="round" stroke-linejoin="round"><path d="M548 266Q537.6 271 523.3 277M548 266Q559.4 271 575.6 278" stroke-width="6.7"/><path d="M548 278V264" stroke-width="14.3"/><path d="M548 268Q560.4 230.3 550 192" stroke-width="9.5"/><path d="M550 192Q527.7 169.8 529.8 138.3M550 192Q579.1 183.2 588 154.2" stroke-width="6.5"/><path d="M529.8 138.3Q511.6 126 494.6 112M529.8 138.3Q540 117.3 533 94.9M588 154.2Q590.3 134.2 603.9 119.4M588 154.2Q607.3 158.2 624.4 148.1" stroke-width="4.4"/><path d="M494.6 112Q478.7 108.5 463.6 114.5M494.6 112Q493.8 94.3 478.7 85.1M533 94.9Q519.7 82.9 522.7 65.2M533 94.9Q543.4 82.7 547.2 67.2M603.9 119.4Q597.3 104.6 605.3 90.5M603.9 119.4Q615.8 110.5 627.4 101.2M624.4 148.1Q638.6 142.6 645.4 128.8M624.4 148.1Q636.2 139.4 639.3 125.1" stroke-width="3"/><path d="M464 114Q469 127 462 138M464 114Q457 106 447 102M479 85Q470 77 458 75M479 85Q476 74 474 62M523 65Q514 56 506 47M523 65Q527 55 529 44M547 67Q545 55 547 44M547 67Q555 56 568 54M605 91Q602 80 595 72M605 91Q612 82 620 74M627 101Q630 89 640 82M627 101Q637 96 648 98M645 129Q645 119 649 109M645 129Q655 126 665 127M639 125Q640 114 643 104M639 125Q646 118 655 115" stroke-width="2"/><path d="M462 138Q462 129 465 120M462 138Q462 129 456 123M447 102Q439 96 430 97M458 75Q450 81 441 78M458 75Q459 65 451 59M474 62Q467 56 462 49M474 62Q479 55 480 45M506 47Q496 51 487 46M529 44Q527 37 522 31M547 44Q547 34 541 28M547 44Q555 38 554 29M568 54Q569 44 577 36M568 54Q576 48 585 51M595 72Q587 69 581 64M595 72Q595 64 594 56M620 74Q623 65 623 56M620 74Q628 73 633 66M640 82Q644 74 642 66M640 82Q647 76 656 71M648 98Q655 95 660 91M648 98Q649 90 657 86M649 109Q641 104 642 95M649 109Q649 101 655 97M665 127Q671 124 677 120M665 127Q671 122 671 115M643 104Q641 96 640 88M643 104Q650 100 653 92M655 115Q657 107 664 104M655 115Q662 116 670 117" stroke-width="1.4"/><path d="M465 120Q459 115 459 108M465 120Q469 113 476 112M456 123Q452 118 445 117M456 123Q454 116 455 109M430 97Q431 104 432 110M430 97Q427 92 420 91M441 78Q440 84 441 90M441 78Q434 80 429 75M451 59Q445 56 441 50M451 59Q453 52 454 45M462 49Q457 45 451 44M462 49Q457 43 458 36M480 45Q482 39 479 33M480 45Q484 41 489 37M487 46Q488 53 489 60M487 46Q483 40 475 39M522 31Q517 29 512 27M522 31Q520 26 520 21M541 28Q537 22 531 20M541 28Q542 21 545 15M554 29Q553 23 552 17M554 29Q558 25 564 24M577 36Q577 29 573 24M577 36Q581 30 589 28M585 51Q587 45 592 39M581 64Q575 64 570 66M581 64Q579 59 574 55M594 56Q588 54 587 47M594 56Q596 51 598 46M623 56Q619 50 614 45M633 66Q636 62 640 58M633 66Q639 67 645 68M642 66Q637 61 638 55M642 66Q646 62 650 58M656 71Q661 66 661 59M656 71Q662 69 668 72M660 91Q664 86 665 81M660 91Q666 89 671 91M657 86Q659 82 657 76M657 86Q661 82 666 82M642 95Q636 94 632 89M642 95Q642 89 643 83M655 97Q655 92 657 87M655 97Q661 97 666 93M677 120Q680 117 683 113M671 115Q671 109 672 104M671 115Q676 114 680 111M640 88Q635 85 633 79M640 88Q643 83 643 77M653 92Q651 87 654 81M653 92Q659 90 665 87M664 104Q664 99 667 95M664 104Q668 100 673 100M670 117Q675 115 679 112M670 117Q674 113 674 107" stroke-width="0.9"/><path d="M459 108Q455 106 452 103M476 112Q477 106 480 102M445 117Q441 116 436 116M445 117Q443 113 439 109M455 109Q450 107 448 103M455 109Q460 107 460 102M432 110Q431 105 434 101M432 110Q428 106 429 101M420 91Q416 91 412 90M420 91Q420 86 418 82M441 90Q441 85 443 81M429 75Q431 80 429 84M429 75Q425 73 423 69M441 50Q436 48 432 49M441 50Q438 45 438 40M454 45Q452 41 450 36M454 45Q456 41 459 37M451 44Q446 46 440 44M451 44Q446 41 444 36M458 36Q453 33 451 29M458 36Q457 30 462 26M479 33Q476 29 473 26M489 37Q491 32 489 28M489 60Q490 55 491 51M489 60Q485 56 485 51M475 39Q477 44 477 50M512 27Q508 28 504 28M512 27Q509 25 506 22M520 21Q517 18 516 15M520 21Q523 18 524 14M531 20Q525 21 521 18M531 20Q527 16 528 11M545 15Q541 11 541 6M545 15Q549 13 552 9M552 17Q550 13 546 10M552 17Q553 12 557 8M564 24Q566 21 568 17M573 24Q568 22 565 18M573 24Q572 19 574 14M589 28Q593 23 597 19M592 39Q591 34 592 29M592 39Q593 34 598 32M570 66Q571 70 571 75M570 66Q566 62 561 62M574 55Q569 54 565 52M574 55Q573 51 572 46M587 47Q584 43 579 43M598 46Q597 43 597 39M598 46Q602 44 605 42M614 45Q609 45 604 44M614 45Q612 40 615 35M640 58Q638 54 641 50M640 58Q643 55 647 55M645 68Q649 68 653 66M645 68Q646 64 649 61M638 55Q638 50 635 47M650 58Q652 53 653 49M650 58Q654 57 658 55M661 59Q658 53 662 48M661 59Q666 58 669 53M668 72Q674 73 678 69M668 72Q670 68 673 64M665 81Q663 78 663 74M665 81Q669 80 672 77M671 91Q674 91 676 88M671 91Q673 88 674 85M657 76Q657 73 654 70M657 76Q661 75 661 71M666 82Q668 78 672 77M666 82Q670 85 674 83M632 89Q628 90 625 88M632 89Q629 86 627 82M643 83Q639 80 638 75M643 83Q644 79 648 77M657 87Q655 84 653 81M657 87Q660 84 660 80M666 93Q669 90 670 86M666 93Q667 89 671 87M683 113Q686 110 685 106M683 113Q687 112 691 112M672 104Q668 102 668 98M672 104Q672 100 675 98M680 111Q683 109 685 106M680 111Q684 110 687 111M633 79Q628 79 625 76M633 79Q633 74 630 69M643 77Q640 74 639 70M643 77Q646 75 647 71M654 81Q651 78 650 74M665 87Q667 83 671 80M665 87Q669 90 673 88M667 95Q666 91 666 87M667 95Q669 91 673 89M673 100Q676 98 678 96M673 100Q676 98 678 95M679 112Q680 108 684 106M679 112Q683 113 687 111M674 107Q675 104 674 100M674 107Q677 105 679 101" stroke-width="0.6"/><path d="M462 138Q462 143 465 148M452 108Q447 108 443 106M452 108Q450 103 446 101M474 62Q477 58 477 53M520 52Q518 48 513 45M520 52Q521 47 523 42M568 54Q571 49 576 47M620 74Q624 70 629 70M648 98Q651 95 654 91M639 106Q643 103 648 103M639 106Q638 101 641 97M609 93Q609 89 607 85M609 93Q613 89 613 85M618 97Q620 94 619 90M618 97Q622 96 627 96M665 127Q665 122 669 120M643 104Q639 100 639 96M655 115Q660 114 662 110M650 153Q654 152 657 149M650 153Q653 150 655 146M642 144Q643 140 642 136M642 144Q646 143 648 138" stroke-width="1"/><path d="M465 148Q465 144 468 142M465 148Q463 144 463 141M443 106Q444 110 444 114M443 106Q442 103 439 102M446 101Q442 100 439 99M446 101Q446 97 445 94M477 53Q479 50 478 46M477 53Q480 50 482 47M513 45Q510 44 506 43M513 45Q510 43 511 39M523 42Q521 38 523 34M523 42Q526 39 529 37M576 47Q577 43 579 39M576 47Q580 49 584 47M629 70Q631 68 634 66M629 70Q630 67 633 65M654 91Q654 88 653 85M654 91Q657 90 660 90M648 103Q650 100 653 99M648 103Q651 103 654 105M641 97Q641 93 638 91M641 97Q645 95 646 91M607 85Q605 82 602 81M607 85Q608 82 607 79M613 85Q613 82 612 79M613 85Q616 84 618 81M619 90Q619 87 617 84M627 96Q629 93 632 92M627 96Q629 94 630 90M611 98Q611 95 610 93M611 98Q613 96 615 95M669 120Q668 116 670 114M669 120Q672 119 673 116M639 96Q638 92 634 91M639 96Q640 93 641 90M662 110Q663 107 665 105M662 110Q665 109 667 107M657 149Q659 146 661 143M657 149Q661 147 664 148M655 146Q654 143 654 140M642 136Q641 132 639 130M648 138Q650 136 651 133" stroke-width="0.7"/><path d="M464 114Q459 109 452 108M523 65Q524 58 520 52M627 101Q633 103 639 106M609 104Q610 98 609 93M609 104Q613 100 618 97M638 156Q644 157 650 153M638 156Q642 150 642 144" stroke-width="1.5"/><path d="M604 119Q605 111 609 104M624 148Q630 153 638 156" stroke-width="2.2"/><path d="M609 104Q610 101 611 98" stroke-width="1.1"/></g>
  <g class="hw-farol" transform="translate(638 155.5)"><path d="M0 0v15" stroke="#0a0418" stroke-width="1.2"/><circle cx="0" cy="21" r="24" fill="url(#hw-brasa)"/><rect x="-4.6" y="15" width="9.2" height="12.5" rx="2.2" fill="#ffb347"/><path d="M-5.2 15.4h10.4" stroke="#0a0418" stroke-width="1.6"/></g>
  <rect class="hw-niebla" x="-60" y="226" width="1560" height="74" fill="url(#hw-niebla-g)"/>
  <path d="M0 282C200 270 420 282 640 278C860 274 1100 284 1440 272V300H0Z" fill="#150b2e"/></svg></div>
""";

    /// <summary>Cementerio del pie de página en tres capas que asoman al llegar al final.</summary>
    public const string FooterLayers = """
<div class="hw-capa-pie" style="--sube:18px"><svg class="hw-svg" viewBox="0 0 1440 170" preserveAspectRatio="xMinYMax slice"><ellipse cx="410" cy="132" rx="160" ry="22" fill="url(#hw-brasa)" opacity=".5"/>
  <ellipse cx="1000" cy="134" rx="190" ry="22" fill="url(#hw-brasa)" opacity=".45"/>
  <g fill="none" stroke="#080312" stroke-linecap="round" stroke-linejoin="round"><path d="M1396 148Q1390.9 153 1384 159M1396 148Q1401.5 153 1409.3 160" stroke-width="3.2"/><path d="M1396 160V146" stroke-width="6.9"/><path d="M1396 150Q1402 128 1396.3 106" stroke-width="4.6"/><path d="M1396.3 106Q1387.4 93.9 1380 80.8M1396.3 106Q1393.6 89.2 1405.9 77.4" stroke-width="3.1"/><path d="M1380 81Q1370 76 1360 74M1380 81Q1380 69 1383 58M1406 77Q1404 65 1398 55M1406 77Q1414 70 1424 64" stroke-width="2.1"/><path d="M1360 74Q1363 82 1361 91M1383 58Q1376 52 1373 44M1383 58Q1391 55 1395 47M1398 55Q1394 46 1385 42M1398 55Q1400 47 1401 39M1424 64Q1426 56 1432 49M1424 64Q1433 60 1441 65" stroke-width="1.4"/><path d="M1361 91Q1361 84 1365 79M1373 44Q1366 44 1360 40M1395 47Q1397 41 1400 36M1395 47Q1402 48 1408 46M1385 42Q1379 40 1373 37M1401 39Q1396 35 1394 28M1401 39Q1404 34 1409 32M1432 49Q1430 44 1428 39M1441 65Q1446 61 1452 62" stroke-width="1"/><path d="M1365 79Q1363 74 1364 69M1365 79Q1369 77 1373 74M1360 40Q1355 38 1351 41M1360 40Q1358 35 1352 33M1400 36Q1398 32 1399 27M1400 36Q1404 34 1407 30M1408 46Q1412 43 1414 38M1373 37Q1374 42 1375 46M1373 37Q1370 34 1369 30M1394 28Q1389 28 1385 25M1394 28Q1396 24 1396 19M1409 32Q1413 30 1414 26M1409 32Q1413 31 1417 33M1401 46Q1402 42 1400 38M1401 46Q1403 42 1407 40M1428 39Q1424 38 1422 35M1428 39Q1430 35 1431 31M1452 62Q1456 61 1460 60M1404 56Q1401 55 1400 51M1414 58Q1413 54 1415 51" stroke-width="0.7"/><path d="M1398 55Q1399 50 1401 46M1408 65Q1407 60 1404 56M1408 65Q1411 62 1414 58" stroke-width="1.1"/><path d="M1406 77Q1407 71 1408 65" stroke-width="1.6"/></g>
  <use href="#hw-murcielago" x="540" y="26" width="28.8" height="21" fill="#07030f"/><use href="#hw-murcielago" x="1130" y="10" width="25" height="18.2" fill="#07030f"/></svg></div>
<div class="hw-capa-pie" style="--sube:44px"><svg class="hw-svg" viewBox="0 0 1440 170" preserveAspectRatio="xMinYMax slice"><path d="M6 142V92l2.6-9 2.6 9v50zM30 142V92l2.6-9 2.6 9v50zM54 142V92l2.6-9 2.6 9v50zM78 142V92l2.6-9 2.6 9v50zM102 142V92l2.6-9 2.6 9v50zM126 142V92l2.6-9 2.6 9v50zM150 142V92l2.6-9 2.6 9v50zM174 142V92l2.6-9 2.6 9v50zM198 142V92l2.6-9 2.6 9v50zM222 142V92l2.6-9 2.6 9v50zM246 142V92l2.6-9 2.6 9v50zM270 142V92l2.6-9 2.6 9v50zM294 142V92l2.6-9 2.6 9v50zM318 142V92l2.6-9 2.6 9v50zM342 142V92l2.6-9 2.6 9v50zM366 142V92l2.6-9 2.6 9v50zM390 142V92l2.6-9 2.6 9v50zM414 142V92l2.6-9 2.6 9v50zM438 142V92l2.6-9 2.6 9v50zM462 142V92l2.6-9 2.6 9v50zM486 142V92l2.6-9 2.6 9v50zM510 142V92l2.6-9 2.6 9v50zM534 142V92l2.6-9 2.6 9v50zM558 142V92l2.6-9 2.6 9v50zM582 142V92l2.6-9 2.6 9v50zM606 142V92l2.6-9 2.6 9v50zM630 142V92l2.6-9 2.6 9v50zM654 142V92l2.6-9 2.6 9v50zM678 142V92l2.6-9 2.6 9v50zM702 142V92l2.6-9 2.6 9v50zM726 142V92l2.6-9 2.6 9v50zM750 142V92l2.6-9 2.6 9v50zM774 142V92l2.6-9 2.6 9v50zM798 142V92l2.6-9 2.6 9v50zM822 142V92l2.6-9 2.6 9v50zM846 142V92l2.6-9 2.6 9v50zM870 142V92l2.6-9 2.6 9v50zM894 142V92l2.6-9 2.6 9v50zM918 142V92l2.6-9 2.6 9v50zM942 142V92l2.6-9 2.6 9v50zM966 142V92l2.6-9 2.6 9v50zM990 142V92l2.6-9 2.6 9v50zM1014 142V92l2.6-9 2.6 9v50zM1038 142V92l2.6-9 2.6 9v50zM1062 142V92l2.6-9 2.6 9v50zM1086 142V92l2.6-9 2.6 9v50zM1110 142V92l2.6-9 2.6 9v50zM1134 142V92l2.6-9 2.6 9v50zM1158 142V92l2.6-9 2.6 9v50zM1182 142V92l2.6-9 2.6 9v50zM1206 142V92l2.6-9 2.6 9v50zM1230 142V92l2.6-9 2.6 9v50zM1254 142V92l2.6-9 2.6 9v50zM1278 142V92l2.6-9 2.6 9v50zM1302 142V92l2.6-9 2.6 9v50zM1326 142V92l2.6-9 2.6 9v50zM1350 142V92l2.6-9 2.6 9v50zM1374 142V92l2.6-9 2.6 9v50zM1398 142V92l2.6-9 2.6 9v50zM1422 142V92l2.6-9 2.6 9v50zM1446 142V92l2.6-9 2.6 9v50z" fill="#080312"/>
  <rect x="0" y="100" width="1440" height="4" fill="#080312"/><rect x="0" y="126" width="1440" height="4" fill="#080312"/>
  <path d="M128 158V121Q128 104 145 104H145Q162 104 162 121V158Z" fill="#120a26" stroke="#2a1655" stroke-width="1.2"/><path d="M468 158V127Q468 112 483 112H483Q498 112 498 127V158Z" fill="#120a26" stroke="#2a1655" stroke-width="1.2"/><path d="M742 158V118Q742 98 762 98H762Q782 98 782 118V158Z" fill="#120a26" stroke="#2a1655" stroke-width="1.2"/><path d="M1246 158V124Q1246 108 1262 108H1262Q1278 108 1278 124V158Z" fill="#120a26" stroke="#2a1655" stroke-width="1.2"/>
  <path d="M630 150V102M616 116h28" stroke="#2a1655" stroke-width="9" stroke-linecap="round"/><path d="M630 150V102M616 116h28" stroke="#120a26" stroke-width="6" stroke-linecap="round"/>
  <path d="M136 124h18M139 132h12M752 118h20M755 127h14" stroke="#2a1655" stroke-width="2" stroke-linecap="round"/>
  <g fill="#05010c">
    <ellipse cx="302" cy="94" rx="11" ry="13"/><circle cx="302" cy="76" r="8.6"/>
    <path d="M294 72 295 59l6 9zM310 72l-1-13-6 9z"/>
    <path d="M312 104q22 4 17-16-1-6 4-8" fill="none" stroke="#05010c" stroke-width="4.2" stroke-linecap="round"/>
  </g>
  <rect x="289" y="104" width="26" height="54" rx="3" fill="#120a26" stroke="#2a1655" stroke-width="1.2"/>
  <g class="hw-ojos"><ellipse cx="298.4" cy="76.6" rx="1.8" ry="2.5" fill="#ffd24a"/><ellipse cx="305.6" cy="76.6" rx="1.8" ry="2.5" fill="#ffd24a"/></g></svg></div>
<div class="hw-capa-pie" style="--sube:80px"><svg class="hw-svg" viewBox="0 0 1440 170" preserveAspectRatio="xMinYMax slice"><g class="hw-calabaza-lit"><circle cx="400" cy="121.2" r="58.8" fill="url(#hw-brasa)"/><use href="#hw-calabaza" x="372" y="96" width="56" height="49"/></g><g class="hw-calabaza-lit"><circle cx="980" cy="121.4" r="54.6" fill="url(#hw-brasa)"/><use href="#hw-calabaza" x="954" y="98" width="52" height="45.5"/></g><g class="hw-calabaza-lit"><circle cx="1028" cy="129.2" r="37.8" fill="url(#hw-brasa)"/><use href="#hw-calabaza" x="1010" y="113" width="36" height="31.5"/></g><g class="hw-calabaza-lit"><circle cx="1339" cy="126.7" r="48.3" fill="url(#hw-brasa)"/><use href="#hw-calabaza" x="1316" y="106" width="46" height="40.3"/></g>
  <path d="M0 148C240 140 520 152 820 146S1240 142 1440 148V170H0Z" fill="#080312"/>
  <rect class="hw-niebla hw-niebla--suelo" x="-60" y="128" width="1560" height="42" fill="url(#hw-niebla-g)"/></svg></div>
""";

    /// <summary>Telaraña de la esquina de la tarjeta de resumen.</summary>
    public const string Cobweb = """
<svg class="hw-telarana" viewBox="0 0 120 120" aria-hidden="true" focusable="false"><path d="M120 0L120 118M120 0L83.5 112.2M120 0L50.6 95.5M120 0L24.5 69.4M120 0L7.8 36.5M120 0L2 0M120 20Q117.5 15.6 113.8 19Q112.8 14.1 108.2 16.2Q108.8 11.2 103.8 11.8Q105.9 7.2 101 6.2Q104.4 2.5 100 0M120 40Q115.1 31.2 107.6 38Q105.7 28.2 96.5 32.4Q97.7 22.3 87.6 23.5Q91.8 14.3 82 12.4Q88.8 4.9 80 0M120 61Q112.5 47.6 101.1 58Q98.1 42.9 84.1 49.4Q85.9 34.1 70.6 35.9Q77.1 21.9 62 18.9Q72.4 7.5 59 0M120 84Q109.6 65.6 94 79.9Q89.9 59.1 70.6 68Q73.1 46.9 52 49.4Q60.9 30.1 40.1 26Q54.4 10.4 36 0M120 110Q106.4 85.8 86 104.6Q80.5 77.4 55.3 89Q58.5 61.5 31 64.7Q42.6 39.5 15.4 34Q34.2 13.6 10 0" fill="none" stroke="currentColor" stroke-width="1" stroke-linecap="round" stroke-linejoin="round"/></svg>
""";

    /// <summary>Araña que cuelga de un hilo en la tarjeta de resumen.</summary>
    public const string HangingSpider = """
<span class="hw-colgante" aria-hidden="true"><svg viewBox="0 0 24 56" focusable="false"><line x1="12" y1="0" x2="12" y2="34" stroke="currentColor" stroke-width=".9"/><use href="#hw-arana" y="30" width="24" height="24"/></svg></span>
""";

    /// <summary>Fantasma que flota sobre la tarjeta del enlace vencido.</summary>
    public const string Ghost = """
<svg class="hw-fantasma" viewBox="0 0 84 100" aria-hidden="true" focusable="false"><use href="#hw-fantasma" width="84" height="100"/></svg>
""";

    /// <summary>Calabaza pequeña antes de la etiqueta de la tarjeta de resumen.</summary>
    public const string PumpkinIcon = """
<svg class="hw-icono-calabaza" viewBox="0 0 96 84" aria-hidden="true" focusable="false"><use href="#hw-calabaza" width="96" height="84"/></svg>
""";

    /// <summary>Hoja de estilos del tema: se agrega después de la hoja base y solo sobrescribe variables y adornos.</summary>
    public const string Css = """
/* ===== Tema Halloween del enlace "Seguimiento de revisión" =====
   Se agrega DESPUÉS de la hoja base y solo sobrescribe variables y adornos:
   la estructura de la página no cambia. Las tarjetas conservan fondo claro
   (papel) para que tablas, estatus y fotos se lean igual que siempre; la noche
   vive en la cabecera, el fondo y el pie. */

.tema-halloween{
  --suelo:#150b2e;
  --papel:#fffaf0; --papel-2:#f8efdf; --papel-linea:#e8d8bc;
  --tinta:#241636; --tinta-suave:#5a4a70; --tinta-tenue:#76698f;
  --violeta:#5b2a86; --violeta-oscuro:#3f1d63; --violeta-claro:#efe6fb; --violeta-linea:#d9c8f2;
  --calabaza:#ff8a1f; --calabaza-alta:#ffa23a;
  --escena-alto:clamp(190px,18vw,330px);
  --bg:var(--suelo); --surface:var(--papel); --surface-2:var(--papel-2);
  --ink:var(--tinta); --ink-soft:var(--tinta-suave); --ink-mute:var(--tinta-tenue); --border:var(--papel-linea);
  --pmm-blue-deep:var(--violeta); --pmm-navy-dark:var(--violeta-oscuro);
  --pmm-blue-pale:var(--violeta-claro); --pmm-blue-line:var(--violeta-linea);
  /* Los estatus conservan su significado (verde / rojo / ámbar / gris); el naranja es solo adorno. */
  --pmm-green:#1b7a3b; --pmm-green-pale:#e3f5e8;
  --pmm-red:#c0262d; --pmm-red-pale:#fdeaea; --pmm-red-line:#f3c1c1; --pmm-red-chip:#f9d3d3;
  --pmm-amber:#8f5400; --pmm-amber-pale:#fff0cf;
  --shadow:0 1px 2px rgba(6,2,18,.4), 0 16px 38px rgba(6,2,18,.42);
  position:relative;
  overflow-x:hidden;
  background:
    radial-gradient(900px 520px at 10% 72%,rgba(124,58,237,.11),transparent 70%),
    radial-gradient(820px 500px at 94% 46%,rgba(255,138,31,.05),transparent 70%),
    var(--suelo);
}
.hw-cielo{position:fixed; top:0; right:0; bottom:0; left:0; z-index:0; overflow:hidden; pointer-events:none;}
.hw-cielo-svg{position:absolute; top:0; left:0; width:100%; height:calc(100% + 120px); display:block; will-change:transform;}

/* --- Cabecera: escena nocturna en capas detrás de la barra superior --- */
.tema-halloween .hw-escena{
  position:absolute; top:0; left:0; right:0; z-index:0; height:var(--escena-alto);
  overflow:hidden; pointer-events:none;
  background:linear-gradient(180deg,#0d0720 0%,#170c36 38%,#2b1458 66%,#5a2a73 86%,#8a3a6c 100%);
}
.tema-halloween .hw-escena::after{
  content:''; position:absolute; left:0; right:0; bottom:0; height:34px;
  background:linear-gradient(180deg,transparent,var(--suelo));
}
/* Cada capa es un div que ocupa todo el recuadro; el parallax mueve el div (no el dibujo), así el navegador solo recoloca una textura. */
.hw-capa,.hw-capa-pie{position:absolute; top:0; right:0; bottom:0; left:0; will-change:transform;}
.hw-svg{
  position:absolute; top:0; bottom:0; left:50%; width:min(100%,1800px); height:100%; display:block;
  transform:translateX(-50%);
  -webkit-mask-image:linear-gradient(90deg,transparent 0,#000 7%,#000 93%,transparent 100%);
  mask-image:linear-gradient(90deg,transparent 0,#000 7%,#000 93%,transparent 100%);
}
.tema-halloween .topbar{position:relative; z-index:2; background:none; padding-top:22px;}
.tema-halloween .brand-mark{color:#003DA5; box-shadow:0 0 0 3px rgba(255,255,255,.14), 0 6px 18px rgba(0,0,0,.4);}
.tema-halloween .brand-name{color:#fff; text-shadow:0 1px 8px rgba(8,3,24,.6);}
.tema-halloween .brand-sub{color:rgba(255,238,208,.8);}
.tema-halloween .shell{position:relative; z-index:2; padding-top:calc(var(--escena-alto) - 100px);}
.tema-halloween .main{position:relative; z-index:1;}

/* --- Tarjetas: franja de color arriba, sombra de noche --- */
.tema-halloween .summary::before,
.tema-halloween .panel::before{content:''; display:block; height:3px; background:linear-gradient(90deg,var(--calabaza),#b25cff 72%,transparent);}
.tema-halloween .summary{position:sticky; border-color:rgba(255,255,255,.08);}
.tema-halloween .panel{border-color:rgba(255,255,255,.08);}
.hw-icono-calabaza{width:15px; height:13px; margin-right:7px; vertical-align:-2px;}
.tema-halloween .eyebrow{color:var(--violeta);}

.hw-telarana{position:absolute; top:3px; right:0; width:112px; height:112px; color:#b89f7c; opacity:.55; pointer-events:none;}
.hw-colgante{position:absolute; top:3px; right:46px; width:22px; height:52px; color:#8a7a9a; transform-origin:50% 0; animation:hw-columpio 5.4s ease-in-out infinite; pointer-events:none;}
.hw-colgante svg{display:block; width:100%; height:100%;}

/* --- Botón principal: calabaza --- */
.tema-halloween .btn-primary{
  background:linear-gradient(180deg,var(--calabaza-alta),var(--calabaza)); color:#2a1203;
  box-shadow:0 8px 20px rgba(255,138,31,.34), inset 0 1px 0 rgba(255,255,255,.4);
}
.tema-halloween .btn-primary:hover{background:linear-gradient(180deg,#ffb04f,#ff962e);}

/* --- Fotos --- */
.tema-halloween .photo-tile{border-color:var(--papel-linea); transition:box-shadow .2s, border-color .2s;}
.tema-halloween .photo-tile:hover{border-color:var(--calabaza); box-shadow:0 0 0 2px rgba(255,138,31,.35), 0 8px 22px rgba(36,22,54,.28);}
.tema-halloween .photo-tile--discrepancia:hover{border-color:var(--pmm-red); box-shadow:0 0 0 2px rgba(192,38,45,.3), 0 8px 22px rgba(36,22,54,.28);}

/* --- Pie: el texto sobre la noche y el cementerio debajo --- */
.tema-halloween .footer-note{position:relative; z-index:2; color:#b9aedb; padding-bottom:10px;}
.tema-halloween .footer-note b{color:#ffd9a8;}
.tema-halloween .footer-brand{color:#9486bd;}
/* overflow:clip (no hidden): hidden convertiría al contenedor en el "scroller" de la animación y las capas no seguirían a la
   ventana. La línea con hidden es para navegadores viejos que no conocen clip (ahí el parallax lo hace el script). */
.tema-halloween .hw-cementerio{position:relative; z-index:1; height:clamp(110px,11.5vw,176px); overflow:hidden; overflow:clip; pointer-events:none;}
.hw-calabaza-lit{animation:hw-brasa 3.6s ease-in-out infinite;}
.hw-calabaza-lit:nth-of-type(2n){animation-duration:4.4s; animation-delay:-1.2s;}

/* --- Visor de fotos --- */
.tema-halloween .viewer{background:rgba(12,6,28,.95);}
.tema-halloween .viewer__download{background:linear-gradient(180deg,var(--calabaza-alta),var(--calabaza)); color:#2a1203;}

/* --- Enlace vencido: el fantasma flota sobre la tarjeta --- */
.tema-halloween .expired-shell{position:relative; z-index:2; padding-top:calc(var(--escena-alto) - 110px);}
.tema-halloween .expired-card{position:relative; padding-top:62px; overflow:visible;}
.tema-halloween.pagina-vencida{min-height:100vh;}
.pagina-vencida .hw-cementerio{position:absolute; left:0; right:0; bottom:0;}
.hw-fantasma{position:absolute; top:-54px; left:50%; width:84px; height:100px; margin-left:-42px; filter:drop-shadow(0 0 16px rgba(214,196,255,.6)); animation:hw-flota 4.2s ease-in-out infinite;}

/* --- Movimiento --- */
.hw-vuelo{animation:hw-cruza var(--d) linear infinite; animation-delay:var(--r);}
.hw-ondula{animation:hw-ondula var(--o) ease-in-out infinite;}
.hw-aletea{transform-box:fill-box; transform-origin:50% 40%; animation:hw-aletea var(--a) ease-in-out infinite alternate;}
.hw-titila{animation:hw-titila var(--t) ease-in-out infinite; animation-delay:var(--r);}
.hw-ventana{fill:#ffbd5a; animation:hw-parpadeo 7s infinite;}
.hw-ventana:nth-child(2n){animation-duration:9.5s; animation-delay:-3s;}
.hw-luces{filter:drop-shadow(0 0 5px rgba(255,170,60,.85));}
.hw-farol{animation:hw-parpadeo 6s infinite;}
.hw-nube{animation:hw-niebla 38s ease-in-out infinite alternate;}
.hw-nube--lenta{animation-duration:60s;}
.hw-niebla{animation:hw-niebla 26s ease-in-out infinite alternate;}
.hw-niebla--suelo{animation-duration:34s;}
.hw-ojos{transform-box:fill-box; transform-origin:50% 50%; animation:hw-parpadea-ojos 6.5s infinite;}
.hw-cara{filter:drop-shadow(0 0 2px rgba(255,200,90,.9));}

@keyframes hw-cruza{from{transform:translateX(var(--x0));}to{transform:translateX(var(--x1));}}
@keyframes hw-ondula{0%,100%{transform:translateY(0);}50%{transform:translateY(-16px);}}
@keyframes hw-aletea{from{transform:scale(1,1);}to{transform:scale(1.05,.6);}}
@keyframes hw-titila{0%,100%{opacity:.22;}50%{opacity:1;}}
@keyframes hw-parpadeo{0%,100%{opacity:1;}7%{opacity:.78;}11%{opacity:1;}52%{opacity:.92;}58%{opacity:.7;}62%{opacity:1;}}
@keyframes hw-columpio{0%,100%{transform:rotate(-7deg);}50%{transform:rotate(7deg);}}
@keyframes hw-niebla{from{transform:translateX(-34px);}to{transform:translateX(34px);}}
@keyframes hw-flota{0%,100%{transform:translateY(0);}50%{transform:translateY(-9px);}}
@keyframes hw-brasa{0%,100%{opacity:1;}50%{opacity:.82;}}
@keyframes hw-parpadea-ojos{0%,92%,100%{transform:scaleY(1);}95%{transform:scaleY(.1);}}

/* --- Parallax al hacer scroll ---
   Cabecera: cada capa baja una fracción (--lag) del recorrido de la cabecera, así lo lejano se queda atrás y lo cercano
   viaja con el contenido. Cielo fijo: deriva despacio hacia arriba durante todo el scroll. Pie: las capas asoman al
   llegar al final, lo más cercano (--sube) desde más abajo. Si el navegador no sabe animar con el scroll, lo hace el
   script del tema con los mismos valores. */
@supports (animation-timeline: scroll()) and (animation-range: 0 1px){
  .hw-capa{animation:hw-desciende linear both; animation-timeline:scroll(root block); animation-range:0 var(--escena-alto);}
  .hw-cielo-svg{animation:hw-deriva linear both; animation-timeline:scroll(root block);}
  .hw-capa-pie{animation:hw-emerge linear both; animation-timeline:view(block); animation-range:entry 0% entry 100%;}
}
@keyframes hw-desciende{to{transform:translate3d(0,calc(var(--lag) * var(--escena-alto)),0);}}
@keyframes hw-deriva{to{transform:translate3d(0,-90px,0);}}
@keyframes hw-emerge{from{transform:translate3d(0,var(--sube),0);}}

@media (max-width:860px){
  .tema-halloween{--escena-alto:clamp(190px,46vw,260px);}
  .tema-halloween .topbar{padding-top:16px;}
  .tema-halloween .shell{padding-top:0;}
  .tema-halloween .summary{position:relative; top:auto; margin-top:calc(var(--escena-alto) - 96px); border-radius:16px; border-left:1px solid rgba(255,255,255,.08); border-right:1px solid rgba(255,255,255,.08); margin-left:14px; margin-right:14px;}
  .tema-halloween .main{padding-top:18px;}
  .hw-telarana{width:92px; height:92px;}
  /* El pie se ancla a la izquierda (donde están el gato y las calabazas) y gana un poco de alto para que se luzca. */
  .tema-halloween .hw-cementerio{height:clamp(140px,40vw,176px);}
  /* En pantallas angostas la luna se recorre a la derecha para no quedar debajo del nombre de la marca. */
  .hw-luna{transform:translateX(215px);}
  .tema-halloween .expired-shell{padding-top:calc(var(--escena-alto) - 100px);}
}

@media (prefers-reduced-motion:reduce){
  .tema-halloween *{animation:none !important;}
  .hw-capa,.hw-capa-pie,.hw-cielo-svg{transform:none !important;}
}

@media print{
  .tema-halloween{background:#fff;}
  .tema-halloween .hw-escena,.tema-halloween .hw-cementerio,.hw-cielo,.hw-telarana,.hw-colgante,.hw-fantasma{display:none !important;}
  .tema-halloween .topbar{background:var(--violeta-oscuro);}
  .tema-halloween .shell{padding-top:36px;}
  .tema-halloween .footer-note{color:var(--ink-mute);}
}
""";

    /// <summary>Parallax de respaldo para navegadores que todavía no animan con el scroll.</summary>
    public const string Script = """
/* Parallax de respaldo para navegadores que todavía no animan con el scroll (el CSS del tema ya lo hace solo
   donde existe `animation-timeline`). Usa los mismos valores que el CSS: --lag en la cabecera, --sube en el pie
   y 90 px de deriva en el cielo fijo. Solo mueve transformaciones; no lee ni cambia texto. */
(function () {
  var raiz = document.documentElement;
  var nativo = window.CSS && CSS.supports && CSS.supports('animation-timeline', 'scroll()') && CSS.supports('animation-range', '0 1px');
  if (nativo) return;
  if (window.matchMedia && matchMedia('(prefers-reduced-motion: reduce)').matches) return;

  var escena = document.querySelector('.hw-escena');
  var pie = document.querySelector('.hw-cementerio');
  var cielo = document.querySelector('.hw-cielo-svg');
  var capas = [].slice.call(document.querySelectorAll('.hw-capa'));
  var capasPie = [].slice.call(document.querySelectorAll('.hw-capa-pie'));
  var retrasos = capas.map(function (capa) { return parseFloat(capa.style.getPropertyValue('--lag')) || 0; });
  var subidas = capasPie.map(function (capa) { return parseFloat(capa.style.getPropertyValue('--sube')) || 0; });
  var esperando = false;

  function mover(elemento, y) { elemento.style.transform = 'translate3d(0,' + y.toFixed(1) + 'px,0)'; }

  function pintar() {
    esperando = false;
    // En iOS el rebote al tope da valores negativos: se ignoran para que la cabecera no se despegue.
    var y = Math.max(0, window.pageYOffset || raiz.scrollTop || 0);
    if (escena) {
      var avance = Math.min(y, escena.offsetHeight || 1);
      capas.forEach(function (capa, i) { mover(capa, avance * retrasos[i]); });
    }
    if (cielo) mover(cielo, -90 * Math.min(1, y / Math.max(1, raiz.scrollHeight - window.innerHeight)));
    if (pie) {
      var caja = pie.getBoundingClientRect();
      var entrada = Math.min(1, Math.max(0, (window.innerHeight - caja.top) / Math.max(1, caja.height)));
      capasPie.forEach(function (capa, i) { mover(capa, (1 - entrada) * subidas[i]); });
    }
  }

  function pedir() {
    if (esperando) return;
    esperando = true;
    requestAnimationFrame(pintar);
  }

  addEventListener('scroll', pedir, { passive: true });
  addEventListener('resize', pedir);
  pintar();
})();
""";
}
