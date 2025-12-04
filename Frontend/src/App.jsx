import { useState } from 'react'
import FileUpload from "./components/FileUpload.jsx"
import { BrowserRouter, Routes, Route, } from 'react-router-dom';
import './styles/base.css'

function App() {
  const [count, setCount] = useState(0)

  return (
    <BrowserRouter>
      <Routes>
        <Route
          path="/"
          element={<FileUpload maxSizeMB={10240} accept={['.xml']} />}
        />
      </Routes>
    </BrowserRouter>
  )
}
export default App
