# Cifrado del transporte cliente-servidor

Con el cifrado activado, todos los mensajes de aplicacion entre los clientes y el servidor se cifran con AES-256-GCM antes de enviarse por TCP o WebSocket. El JSON actual del protocolo (`REGISTER`, `METRICS`, `COMMAND`, `ACK`, `CONFIG_INTERVAL`, `CLIENT_CONFIG` y `ERROR`) se conserva dentro del sobre cifrado.

La red solo transporta mensajes con el formato `CNS1:` seguido de Base64 de `nonce + tag + ciphertext`. El prefijo no contiene datos de negocio y el resto no revela el JSON ni las metricas. Cada mensaje utiliza un nonce aleatorio de 96 bits y un tag de autenticacion de 128 bits.

## Modo temporal de compatibilidad

Mientras el servidor hosteado antiguo siga activo, el transporte funciona en texto plano por defecto para no interrumpir las pruebas restantes. Para activar el cifrado cuando servidor y clientes actualizados ya esten publicados, configure en todos:

```powershell
$env:CNS_STORAGE_CLUSTER_ENABLE_ENCRYPTION = '1'
```

Al activarlo, se vuelve obligatorio usar la misma clave AES-256-GCM en todos los extremos conforme a las secciones siguientes.

## Clave compartida para instalaciones distribuidas

Para servidor y clientes en equipos o cuentas de Windows distintas, configure en todos la misma variable de entorno:

```powershell
$bytes = New-Object byte[] 32
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
$key = [Convert]::ToBase64String($bytes)
$env:CNS_STORAGE_CLUSTER_ENCRYPTION_KEY = $key
[Environment]::SetEnvironmentVariable('CNS_STORAGE_CLUSTER_ENCRYPTION_KEY', $key, 'User')
```

Guarde ese valor en un gestor de secretos y distribuyalo por un canal seguro. Para un servicio de Windows o IIS, configure la variable en el contexto de la cuenta que ejecuta el servicio (normalmente en el nivel `Machine`) y reinicie el proceso. Para cada cliente, use exactamente el mismo valor y reinicie la aplicacion.

En desarrollo local, si no existe la variable, la primera aplicacion crea una clave AES-256 aleatoria en `%LOCALAPPDATA%\CNS.StorageCluster\secrets\transport-key.base64`. Si el perfil de usuario no permite escritura, la guarda bajo `.cns-storagecluster\secrets` junto al ejecutable. El cliente y el servidor ejecutados con la misma cuenta reutilizan ese archivo. No copie el archivo al repositorio ni lo use como mecanismo de distribucion entre equipos.

No guarde la clave en el repositorio, en `appsettings.json`, en capturas de pantalla ni en los logs. Una clave invalida o que no coincide hace que el proceso rechace la conexion; nunca se envian datos en texto plano.

## Despliegue y rotacion

Actualice servidor y clientes conjuntamente: las versiones anteriores enviaban JSON en claro y seran rechazadas por la version cifrada. Para rotar la clave, distribuya la nueva clave a todos los clientes y al servidor, y reinicelos dentro de una ventana coordinada.
